#region "copyright"

/*
    Copyright (c) 2026 OpenAstro contributors

    This file is part of OpenAstro Ara.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NUnit.Framework;
using OpenAstroAra.Core.Utility;
using System;

namespace OpenAstroAra.Test {

    /// <summary>
    /// Pins <see cref="PlotTime.ToDouble(DateTime)"/> to the numbers OxyPlot 2.2.0's
    /// DateTimeAxis.ToDouble produced for the same inputs (#1386 part 2 replaced it). The
    /// altitude, twilight and moon series go to clients on this axis, so the values are
    /// hard-coded here rather than derived; a drift in the origin or in DateTimeKind
    /// handling must fail this test, not move the expectation with it.
    /// </summary>
    [TestFixture]
    public class PlotTimeTest {

        [TestCase(2026, 10, 18, 3, 30, 15, 0, DateTimeKind.Local, 46313.146006944444)]
        [TestCase(2026, 10, 18, 3, 30, 15, 0, DateTimeKind.Utc, 46313.146006944444)]
        [TestCase(2026, 10, 18, 3, 30, 15, 0, DateTimeKind.Unspecified, 46313.146006944444)]
        [TestCase(2026, 10, 18, 3, 30, 15, 123, DateTimeKind.Unspecified, 46313.14600836806)]
        [TestCase(2000, 1, 1, 12, 0, 0, 0, DateTimeKind.Unspecified, 36526.5)]
        [TestCase(1899, 12, 30, 0, 0, 0, 0, DateTimeKind.Unspecified, 0.0)]
        public void ToDouble_matches_OxyPlot_DateTimeAxis(int y, int mo, int d, int h, int mi, int s, int ms, DateTimeKind kind, double expected) {
            var value = new DateTime(y, mo, d, h, mi, s, ms, kind);
            Assert.That(PlotTime.ToDouble(value), Is.EqualTo(expected));
        }

        [Test]
        public void ToDouble_MinValue_is_what_OxyPlot_gave_not_ToOADate() {
            // OxyPlot: -693593. DateTime.ToOADate would say 0 here; the series never carried that.
            Assert.That(PlotTime.ToDouble(DateTime.MinValue), Is.EqualTo(-693593.0));
            Assert.That(DateTime.MinValue.ToOADate(), Is.EqualTo(0.0));
        }

        [Test]
        public void ToDouble_nullable_is_NaN_for_null_and_the_same_double_otherwise() {
            Assert.That(PlotTime.ToDouble((DateTime?)null), Is.NaN);
            DateTime? when = new DateTime(2000, 1, 1, 12, 0, 0);
            Assert.That(PlotTime.ToDouble(when), Is.EqualTo(36526.5));
        }
    }
}
