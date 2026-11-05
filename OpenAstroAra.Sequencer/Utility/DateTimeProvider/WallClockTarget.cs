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

namespace OpenAstroAra.Sequencer.Utility.DateTimeProvider {

    /// <summary>Shared by WaitForTime and TimeCondition (#1576).</summary>
    internal static class WallClockTarget {

        /// <summary>
        /// A plain clock time (<paramref name="nightAnchored"/> false) is its next occurrence from
        /// <paramref name="now"/>. A night-anchored time keeps the rollover rule: before the rollover a
        /// target at or after it belongs to the night that began yesterday, after it a target before
        /// it belongs to tomorrow's.
        /// </summary>
        public static DateTime Resolve(DateTime now, int hours, int minutes, int seconds, TimeOnly rollover, bool nightAnchored) {
            var then = new DateTime(now.Year, now.Month, now.Day, hours, minutes, seconds);
            if (!nightAnchored) {
                return then < now ? then.AddDays(1) : then;
            }
            var timeOnlyNow = TimeOnly.FromDateTime(now);
            var timeOnlyThen = TimeOnly.FromDateTime(then);
            if (timeOnlyNow < rollover && timeOnlyThen >= rollover) {
                return then.AddDays(-1);
            }
            if (timeOnlyNow >= rollover && timeOnlyThen < rollover) {
                return then.AddDays(1);
            }
            return then;
        }
    }
}
