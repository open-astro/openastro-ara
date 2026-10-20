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
using System.Diagnostics.CodeAnalysis;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// #1185 — the cooler's heat-sink temperature (ASCOM <c>HeatSinkTemperature</c>) is an optional,
/// slow-moving figure that nothing in the daemon acts on yet, so the status tick must not pay a
/// blocking Alpaca round-trip for it on every pass. One instance per <see cref="CameraService"/>:
/// it reads only while the cooler is on, once every <see cref="Every"/> passes (the value in
/// between is the last one read), and a driver that answers NotImplemented is not asked again
/// until the next connect (<see cref="Reset"/>).
/// </summary>
internal sealed class HeatSinkProbe {

    /// <summary>Status passes between two reads while the cooler is on (the tick is ~1 s).</summary>
    public const int Every = 10;

    private int _tick;
    private bool _unsupported;
    private double? _last;

    /// <summary>Whether the connected driver answered NotImplemented (tests).</summary>
    public bool Unsupported => _unsupported;

    /// <summary>Forget the latch and the cached value: a new driver may implement the property.</summary>
    public void Reset() {
        _tick = 0;
        _unsupported = false;
        _last = null;
    }

    /// <summary>The heat-sink temperature to report this pass: null when the cooler is off or the
    /// driver has no such property; otherwise the value from <paramref name="read"/> on the first
    /// pass and every <see cref="Every"/>th after, with the previous value in between.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary like the other optional camera properties: a transient driver failure reads as \"not reported\" this pass rather than failing the whole status tick.")]
    public double? Read(bool coolerOn, Func<double> read) {
        ArgumentNullException.ThrowIfNull(read);
        if (_unsupported || !coolerOn) {
            _tick = 0; // the next cooler-on pass reads at once
            return null;
        }
        if (_tick++ % Every != 0) {
            return _last;
        }
        try {
            _last = read();
        } catch (ASCOM.NotImplementedException) {
            _unsupported = true;
            _last = null;
        } catch (Exception) {
            _last = null;
        }
        return _last;
    }
}
