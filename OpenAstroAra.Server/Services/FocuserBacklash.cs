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
using System;
using System.Collections.Generic;
using System.Threading;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// #1541 — backlash compensation for the focuser service's move path. When a move reverses direction
/// relative to the last one, it first overshoots the target by the backlash for the new direction and then
/// returns, so the gear train has taken up its slack before the final approach. Direction +1 is OUT (higher
/// position), -1 is IN. Pure planning here; <see cref="FocuserService"/> executes the legs.
/// </summary>
internal static class FocuserBacklash {

    private static readonly AsyncLocal<bool> Suppressed = new();

    internal static bool IsSuppressed => Suppressed.Value;

    /// <summary>Within the returned scope the focuser service does not compensate: the autofocus sweep picks its
    /// own approach direction for every probe.</summary>
    internal static IDisposable Suppress() {
        var previous = Suppressed.Value;
        Suppressed.Value = true;
        return new Scope(previous);
    }

    private sealed class Scope(bool previous) : IDisposable {
        public void Dispose() => Suppressed.Value = previous;
    }

    /// <summary>Backlash steps for a move in <paramref name="direction"/>. The sweep's measured backlash
    /// (<see cref="FocusCalibrationDto.BacklashUpSteps"/>, an upward = OUT figure) wins for OUT when present;
    /// otherwise the wizard value. Never negative.</summary>
    internal static int StepsFor(int direction, AutofocusSettingsDto settings, FocusCalibrationDto? calibration) {
        if (direction > 0 && calibration?.BacklashUpSteps is double measured && double.IsFinite(measured)) {
            return Math.Max(0, (int)Math.Round(measured));
        }
        return Math.Max(0, direction > 0 ? settings.BacklashOutSteps : settings.BacklashInSteps);
    }

    /// <summary>The legs to drive for a move to <paramref name="target"/> and the direction it travels (0 =
    /// unknown or no motion). Absolute focusers get absolute positions; a relative focuser (<paramref name="from"/>
    /// unknown) gets signed offsets. A single leg when nothing needs compensating.</summary>
    internal static (int[] Legs, int Direction) Plan(
            bool absolute, int? from, int target, int lastDirection, Func<int, int> backlashFor, int minPosition, int maxPosition) {
        var direction = absolute ? (from is int f ? Math.Sign((long)target - f) : 0) : Math.Sign(target);
        if (direction == 0 || lastDirection == 0 || direction == lastDirection) {
            return (new[] { target }, direction);
        }
        var steps = backlashFor(direction);
        if (steps <= 0) {
            return (new[] { target }, direction);
        }
        var over = (long)target + direction * steps;
        if (absolute && maxPosition > 0) {
            over = Math.Clamp(over, minPosition, maxPosition);
        }
        if (over == target || over > int.MaxValue || over < int.MinValue) {
            return (new[] { target }, direction); // no room past the target (travel stop): nothing to take up
        }
        // A relative focuser takes offsets, so the return leg is the offset back; an absolute one the target itself.
        var back = absolute ? target : (int)(target - over);
        return (new[] { (int)over, back }, direction);
    }

    /// <summary>The runtime as clients see it: <c>backlash_compensating</c> for the whole compensated move.</summary>
    internal static FocuserStateDto Report(FocuserStateDto runtime, bool compensating) =>
        compensating ? runtime with { State = "backlash_compensating" } : runtime;
}
