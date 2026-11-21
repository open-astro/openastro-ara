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

/// <summary>
/// One guide axis running away against its maximum pulses: the rule the polar alignment residual
/// (<see cref="PaResidualTracker"/>, #1889) and the Dec runaway watch (#1589, GuiderService.DecRunaway)
/// share. Pure and single-threaded: the caller serialises the calls.
/// </summary>
/// <remarks>
/// A runaway: <see cref="Steps"/> steps in a row with the pulse at (within 5 % of) the largest one the
/// run has sent and the distance never shrinking, ending at least 3x and 1 px above where the streak
/// began. A steady drift also sits at one pulse length with a flat distance, so the growth is what tells
/// them apart. 10 frames is ~20 s at PHD2's usual 2 s exposure: a gust or a bad frame recovers well
/// inside it, a mount that is not tracking never does (rig night 2026-11-14: RA ran 0.5 to 37 px with
/// every pulse at 2500 ms). An axis that is not guided (zero pulses) never counts.
/// </remarks>
internal sealed class GuideAxisRunaway {

    internal const int Steps = 10;
    private const double PinnedFraction = 0.95, GrowthFactor = 3, GrowthMinPx = 1;

    private int _run;

    /// <summary>The largest pulse (ms, unsigned) seen since the last <see cref="Clear"/>.</summary>
    public double MaxPulse { get; private set; }

    /// <summary>The distance (px, unsigned) where the current streak began.</summary>
    public double StartDistance { get; private set; }

    /// <summary>The last step's distance (px, signed as given).</summary>
    public double LastDistance { get; private set; }

    /// <summary>One guide step on this axis: its distance (px) and its pulse (ms, either sign).
    /// True when the streak is a runaway.</summary>
    public bool Step(double distancePx, double durationMs) {
        var pulse = System.Math.Abs(durationMs);
        MaxPulse = System.Math.Max(MaxPulse, pulse);
        var pinned = pulse > 0 && pulse >= PinnedFraction * MaxPulse;
        var growing = System.Math.Abs(distancePx) >= System.Math.Abs(LastDistance);
        LastDistance = distancePx;
        _run = pinned && growing ? _run + 1 : 0;
        var now = System.Math.Abs(distancePx);
        if (_run == 1) {
            StartDistance = now;
        }
        return _run >= Steps && now >= GrowthFactor * StartDistance && now - StartDistance >= GrowthMinPx;
    }

    public void Clear() {
        MaxPulse = 0;
        LastDistance = 0;
        StartDistance = 0;
        _run = 0;
    }
}
