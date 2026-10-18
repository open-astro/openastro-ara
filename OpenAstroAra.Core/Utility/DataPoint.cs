#region "copyright"

/*
    Copyright (c) 2026 OpenAstro contributors

    This file is part of OpenAstro Ara.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;

namespace OpenAstroAra.Core.Utility {

    /// <summary>
    /// A plain (x, y) sample on an altitude / twilight chart. Replaces OxyPlot's DataPoint
    /// (#1386): the daemon only ever built these series for the wire, never plotted them.
    /// </summary>
    public readonly record struct DataPoint(double X, double Y);

    /// <summary>
    /// The chart time axis OxyPlot's DateTimeAxis defined: fractional days since
    /// 1899-12-30 00:00 (the OLE Automation date origin), ignoring <see cref="DateTimeKind"/>.
    /// Kept bit-identical so clients reading the altitude series see the same numbers.
    /// </summary>
    public static class PlotTime {
        private static readonly DateTime Origin = new(1899, 12, 30, 0, 0, 0, DateTimeKind.Unspecified);

        public static double ToDouble(DateTime value) => (value - Origin).TotalDays;

        /// <summary>
        /// For the rise/set series, whose event times are nullable. Every caller checks
        /// HasValue first; an absent time has no place on the axis, hence NaN.
        /// </summary>
        public static double ToDouble(DateTime? value) => value.HasValue ? ToDouble(value.Value) : double.NaN;
    }
}
