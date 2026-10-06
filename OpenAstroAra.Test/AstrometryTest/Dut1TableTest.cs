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
using System.IO;
using NUnit.Framework;
using OpenAstroAra.Astrometry;

namespace OpenAstroAra.Test.AstrometryTest {

    /// <summary>
    /// #1190 — UT1-UTC comes from the bundled IERS Bulletin A snapshot instead of a hard 0:
    /// a date inside the table returns the table's value, a date past its end falls back to the
    /// bulletin's long-term formula (bounded, never throws), and the AstroUtil call site consumes it.
    /// </summary>
    [TestFixture]
    public class Dut1TableTest {
        private const double Tolerance = 1e-6;

        // Row from OpenAstroAra.Astrometry/External/iers-dut1.tsv (last IERS-observed day of the
        // 2026-09-24 bulletin). Update alongside the table if the snapshot is regenerated.
        private static readonly DateTime KnownDate = new DateTime(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc);
        private const double KnownDut1 = -0.0134728;

        private static Dut1Table Synthetic(string extraHeader = "", params string[] rows) {
            var text = "# test table\n" + extraHeader + string.Join("\n", rows) + "\n";
            return Dut1Table.Parse(new StringReader(text));
        }

        [Test]
        public void Bundled_KnownDate_ReturnsTableValue() {
            var table = Dut1Table.Bundled;

            Assert.That(table.Count, Is.GreaterThan(365));
            Assert.That(table.FirstDate, Is.LessThanOrEqualTo(KnownDate));
            Assert.That(table.LastDate, Is.GreaterThan(KnownDate));
            Assert.That(table.Lookup(KnownDate), Is.EqualTo(KnownDut1).Within(Tolerance));
        }

        [Test]
        public void Bundled_ExposesProvenance() {
            var table = Dut1Table.Bundled;

            Assert.That(table.Bulletin, Does.Contain("Vol."));
            Assert.That(table.LastObservedDate, Is.GreaterThanOrEqualTo(KnownDate));
            Assert.That(table.HasExtrapolation, Is.True);
        }

        [Test]
        public void Lookup_MidDay_InterpolatesBetweenNeighbours() {
            var table = Synthetic("", "61306\t-0.0123490\tI", "61307\t-0.0134728\tI");

            var noon = new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);

            Assert.That(table.Lookup(noon), Is.EqualTo((-0.0123490 + -0.0134728) / 2).Within(Tolerance));
        }

        [Test]
        public void Lookup_LocalTime_IsConvertedToUtc() {
            var table = Synthetic("", "61306\t-0.0123490\tI", "61307\t-0.0134728\tI");
            var noonUtc = new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);

            Assert.That(table.Lookup(noonUtc.ToLocalTime()), Is.EqualTo(table.Lookup(noonUtc)).Within(Tolerance));
        }

        [Test]
        public void Lookup_AcrossLeapSecondStep_HoldsThePreviousDay() {
            // A 1 s jump between consecutive rows is a leap second, not a slope to interpolate.
            var table = Synthetic("", "57753\t-0.5912000\tI", "57754\t0.4085000\tI");

            var lateEvening = new DateTime(2016, 12, 31, 23, 0, 0, DateTimeKind.Utc);

            Assert.That(table.Lookup(lateEvening), Is.EqualTo(-0.5912000).Within(Tolerance));
        }

        [Test]
        public void Lookup_PastTableEnd_UsesBulletinFormula_AndNeverThrows() {
            var table = Synthetic("# extrapolation_params=-0.0933 -0.00030 61315\n", "61679\t-0.1474985\tP", "61680\t-0.1478001\tP");

            var mjd = 61680 + 30;
            var date = Dut1Table.MjdToDateTime(mjd);
            double Formula(double m) => -0.0933 - 0.00030 * (m - 61315) - Dut1Table.Ut2MinusUt1(m);
            // Anchored to the last table value (#1232): formula(mjd) + (last - formula(LastMjd)).
            var expected = Formula(mjd) + (-0.1478001 - Formula(61680));

            Assert.That(table.Lookup(date), Is.EqualTo(expected).Within(Tolerance));
            Assert.That(table.IsCovered(date), Is.False);
        }

        [Test]
        public void Lookup_FarPastTableEnd_IsBounded() {
            var table = Synthetic("# extrapolation_params=-0.0933 -0.00030 61315\n", "61680\t-0.1478001\tP");

            var tenYearsOn = Dut1Table.MjdToDateTime(61680 + 3650);
            var atBound = Dut1Table.MjdToDateTime(61680 + Dut1Table.ExtrapolationDays);
            var value = table.Lookup(tenYearsOn);

            Assert.That(double.IsFinite(value), Is.True);
            Assert.That(Math.Abs(value), Is.LessThanOrEqualTo(0.9));
            Assert.That(value, Is.EqualTo(table.Lookup(atBound)).Within(Tolerance), "extrapolation stops growing at the bound");
        }

        [Test]
        public void Lookup_PastTableEnd_WithoutFormula_HoldsLastValue() {
            var table = Synthetic("", "61679\t-0.1474985\tP", "61680\t-0.1478001\tP");

            Assert.That(table.Lookup(new DateTime(2028, 6, 1, 0, 0, 0, DateTimeKind.Utc)), Is.EqualTo(-0.1478001).Within(Tolerance));
        }

        [Test]
        public void Lookup_BeforeTableStart_HoldsFirstValue() {
            var table = Synthetic("", "60310\t0.0087837\tI", "60311\t0.0084956\tI");

            Assert.That(table.Lookup(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)), Is.EqualTo(0.0087837).Within(Tolerance));
        }

        [Test]
        public void Bundled_FarFuture_DoesNotThrow() {
            var value = Dut1Table.Bundled.Lookup(new DateTime(2040, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            Assert.That(double.IsFinite(value), Is.True);
            Assert.That(Math.Abs(value), Is.LessThanOrEqualTo(0.9));
        }

        [Test]
        public void Empty_ReturnsZero() {
            var table = Dut1Table.Parse(new StringReader("# nothing\n"));

            Assert.That(table.Count, Is.EqualTo(0));
            Assert.That(table.Lookup(KnownDate), Is.EqualTo(0d));
        }

        [Test]
        public void Parse_SkipsMalformedRows() {
            var table = Synthetic("", "garbage", "61306\tnot-a-number\tI", "61307\t-0.0134728\tI", "");

            Assert.That(table.Count, Is.EqualTo(1));
            Assert.That(table.Lookup(KnownDate), Is.EqualTo(KnownDut1).Within(Tolerance));
        }

        // #1232: a byte-exact excerpt of the real finals2000A.all (fixtures/iers-finals2000A-excerpt.txt,
        // copied out of the 2026-10-05 download) pins the fixed-width offsets the parser uses, so a
        // one-column drift against the live file can no longer pass by matching a hand-typed fixture.
        [Test]
        public void Parse_AcceptsRawFinals2000AFormat_FromARealFileExcerpt() {
            using var reader = new StreamReader(FixturePath("iers-finals2000A-excerpt.txt"));

            var table = Dut1Table.Parse(reader, "fixture");

            // 24 1 1, 24 1 2, 26 923..925, 27 930, 2710 4 (last P row) parse; the empty-flag row after it is skipped.
            Assert.That(table.FirstMjd, Is.EqualTo(60310));
            Assert.That(table.LastMjd, Is.EqualTo(61682));
            Assert.That(table.LastObservedMjd, Is.EqualTo(61308));
            Assert.That(table.Lookup(Dut1Table.MjdToDateTime(60310)), Is.EqualTo(0.0087837).Within(Tolerance));
            Assert.That(table.Lookup(Dut1Table.MjdToDateTime(60311)), Is.EqualTo(0.0084956).Within(Tolerance));
            Assert.That(table.Lookup(Dut1Table.MjdToDateTime(61306)), Is.EqualTo(-0.0123478).Within(Tolerance));
            Assert.That(table.Lookup(Dut1Table.MjdToDateTime(61308)), Is.EqualTo(-0.0148412).Within(Tolerance));
            Assert.That(table.Lookup(Dut1Table.MjdToDateTime(61678)), Is.EqualTo(-0.1631985).Within(Tolerance));
            Assert.That(table.Lookup(Dut1Table.MjdToDateTime(61682)), Is.EqualTo(-0.1626945).Within(Tolerance));
        }

        private static string FixturePath(string name) {
            // The csproj copies the fixture next to the test assembly; the walk-up is for an in-place build.
            var copied = Path.Combine(AppContext.BaseDirectory, "fixtures", name);
            if (File.Exists(copied)) {
                return copied;
            }
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null) {
                var candidate = Path.Combine(dir.FullName, "OpenAstroAra.Test", "fixtures", name);
                if (File.Exists(candidate)) {
                    return candidate;
                }
                dir = dir.Parent;
            }
            throw new FileNotFoundException("OpenAstroAra.Test/fixtures/" + name + " not found above " + AppContext.BaseDirectory);
        }

        // #1232: the formula is anchored to the table's last value, so there is no step at the seam.
        [Test]
        public void Lookup_PastTableEnd_ContinuesSmoothlyFromTheLastValue() {
            var table = Synthetic("# extrapolation_params=-0.0933 -0.00030 61315\n", "61679\t-0.1474985\tP", "61680\t-0.1478001\tP");

            var lastValue = table.Lookup(Dut1Table.MjdToDateTime(61680));
            var oneDayOn = table.Lookup(Dut1Table.MjdToDateTime(61681));
            var formulaAtEnd = -0.0933 - 0.00030 * (61680 - 61315) - Dut1Table.Ut2MinusUt1(61680);

            Assert.That(Math.Abs(formulaAtEnd - lastValue), Is.GreaterThan(0.01), "the fixture reproduces the ~26 ms seam the raw formula has");
            Assert.That(oneDayOn, Is.EqualTo(lastValue).Within(0.001), "one day past the end moves by the formula's daily slope, not by the seam");
        }

        // #1232: the last row covers its whole day; only the next midnight starts the extrapolation.
        [Test]
        public void Lookup_InsideTheLastDay_IsCoveredAndHoldsTheLastValue() {
            var table = Synthetic("# extrapolation_params=-0.0933 -0.00030 61315\n", "61679\t-0.1474985\tP", "61680\t-0.1478001\tP");

            var lastNoon = Dut1Table.MjdToDateTime(61680.5);
            var nextMidnight = Dut1Table.MjdToDateTime(61681);

            Assert.That(table.IsCovered(table.LastDate), Is.True);
            Assert.That(table.IsCovered(lastNoon), Is.True);
            Assert.That(table.Lookup(lastNoon), Is.EqualTo(-0.1478001).Within(Tolerance));
            Assert.That(table.IsCovered(nextMidnight), Is.False);
        }

        // #1232: a pathological override path must fall back, not poison the Lazy factory.
        [TestCase("a\0b")]
        [TestCase("/definitely/not/here/finals2000A.all")]
        [TestCase("")]
        public void Load_WithABadOverridePath_FallsBackToTheEmbeddedTable(string overridePath) {
            var table = Dut1Table.Load(overridePath);

            Assert.That(table.Source, Is.EqualTo("embedded"));
            Assert.That(table.Count, Is.GreaterThan(365));
        }

        [Test]
        public void DatabaseInteraction_GetUt1Utc_ReturnsBundledValue() {
            var db = new DatabaseInteraction();

            var value = db.GetUt1Utc(KnownDate, default).GetAwaiter().GetResult();

            Assert.That(value, Is.EqualTo(KnownDut1).Within(Tolerance));
        }

        [Test]
        public void AstroUtil_DeltaUT_ConsumesBundledTable() {
            Assert.That(AstroUtil.DeltaUT(KnownDate), Is.EqualTo(KnownDut1).Within(Tolerance));
        }

        [Test]
        public void MjdRoundTrip() {
            Assert.That(Dut1Table.DateTimeToMjd(KnownDate), Is.EqualTo(61307).Within(1e-9));
            Assert.That(Dut1Table.MjdToDateTime(61307), Is.EqualTo(KnownDate));
        }
    }
}
