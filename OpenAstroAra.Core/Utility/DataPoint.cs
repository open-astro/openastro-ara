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
    /// The chart time axis OxyPlot's DateTimeAxis defined: fractional days since 1899-12-30
    /// 00:00 (the OLE Automation date origin), ignoring <see cref="DateTimeKind"/>. The
    /// arithmetic is OxyPlot's own (days since 1899-12-31, plus one), not the equivalent
    /// subtraction from 1899-12-30: the two differ in the last bit for about one input in
    /// 60,000, and clients reading the altitude series must keep seeing the same doubles.
    /// </summary>
    public static class PlotTime {
        private static readonly DateTime TimeOrigin = new(1899, 12, 31, 0, 0, 0, DateTimeKind.Unspecified);

        public static double ToDouble(DateTime value) => (value - TimeOrigin).TotalDays + 1;

        /// <summary>
        /// For the rise/set series, whose event times are nullable. Every caller checks
        /// HasValue first; an absent time has no place on the axis, hence NaN.
        /// </summary>
        public static double ToDouble(DateTime? value) => value.HasValue ? ToDouble(value.Value) : double.NaN;
    }
}
