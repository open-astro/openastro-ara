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
/// #1541 — backlash compensation for the focuser service's move path (NINA's Absolute method). When a move
/// reverses direction relative to the last one, the backlash for the new direction is added to the move, in
/// that direction. Direction +1 is OUT (higher position), -1 is IN. Pure planning here;
/// <see cref="FocuserService"/> executes the move and tracks the offset.
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

    /// <summary>One planned move: the position (or, for a relative focuser, offset) to send the hardware, the
    /// backlash steps included in it, the direction the move travels (0 = unknown or no motion) and the standing
    /// offset after the move (absolute focusers: hardware minus logical target).</summary>
    internal readonly record struct Move(int HardwareTarget, int Extra, int Direction, int Offset);

    /// <summary>NINA's Absolute method: on a direction change the move goes <c>backlash</c> steps further in the
    /// NEW direction and stops there, with no return leg. <paramref name="offset"/> is the sum of the extra steps
    /// already added, so the hardware sits at <c>logical + offset</c> and the user's target scale never shifts.
    /// A relative focuser (<paramref name="absolute"/> false) takes the offset as it is.
    /// Extra steps that would leave the travel are skipped, and the standing offset is clamped to the travel:
    /// a logical target at a travel stop pulls the offset back (hardware = the stop), so the reported position
    /// stays equal to the requested target and the hardware never gets an out-of-range move.</summary>
    internal static Move Plan(
            bool absolute, int? from, int target, int lastDirection, Func<int, int> backlashFor, int offset, int minPosition, int maxPosition) {
        var direction = absolute ? (from is int f ? Math.Sign((long)target - f) : 0) : Math.Sign(target);
        var plain = absolute ? (long)target + offset : target;
        if (absolute && maxPosition > 0) {
            plain = Math.Clamp(plain, minPosition, maxPosition);
        }
        var steps = direction != 0 && lastDirection != 0 && direction != lastDirection ? backlashFor(direction) : 0;
        var hardware = plain + direction * steps;
        if (steps <= 0 || (absolute && maxPosition > 0 && (hardware < minPosition || hardware > maxPosition)) || hardware > int.MaxValue || hardware < int.MinValue) {
            return new Move((int)plain, 0, direction, absolute ? (int)plain - target : offset);
        }
        return new Move((int)hardware, direction * steps, direction, absolute ? (int)hardware - target : offset);
    }

    /// <summary>The runtime as clients see it: <c>backlash_compensating</c> for the whole compensated move.</summary>
    internal static FocuserStateDto Report(FocuserStateDto runtime, bool compensating) =>
        compensating ? runtime with { State = "backlash_compensating" } : runtime;
}
