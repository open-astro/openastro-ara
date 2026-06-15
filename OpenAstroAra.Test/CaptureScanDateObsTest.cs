#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NUnit.Framework;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §50/§28 — the orphan-scan path reads a recovered frame's capture time from the FITS <c>DATE-OBS</c> header.
    /// FITS defines DATE-OBS as UTC, but the value is usually written without a zone designator. These pin the
    /// contract that <see cref="CaptureScanService.ParseDateObs"/> always yields a UTC instant (offset zero) so the
    /// stored <c>captured_utc</c> is uniformly <c>…+00:00</c> — the form the lexicographic <c>since</c>/ORDER BY
    /// comparisons and the SqliteFrameRepository write path both rely on.
    /// </summary>
    [TestFixture]
    public class CaptureScanDateObsTest {

        private static Dictionary<string, string> Headers(string dateObs) =>
            new() { ["DATE-OBS"] = dateObs };

        [Test]
        public void Zoneless_DATE_OBS_is_read_as_UTC_not_local() {
            // The bug this fixes: a bare TryParse assumed local, mis-shifting the instant by the host's UTC offset.
            var dt = CaptureScanService.ParseDateObs(Headers("2026-05-30T03:14:00"));
            Assert.That(dt, Is.Not.Null);
            Assert.That(dt!.Value.Offset, Is.EqualTo(TimeSpan.Zero), "zoneless DATE-OBS is UTC per the FITS spec");
            Assert.That(dt.Value.UtcDateTime, Is.EqualTo(new DateTime(2026, 5, 30, 3, 14, 0, DateTimeKind.Utc)));
        }

        [Test]
        public void Explicit_Z_round_trips_to_UTC() {
            var dt = CaptureScanService.ParseDateObs(Headers("2026-05-30T03:14:00Z"));
            Assert.That(dt!.Value.Offset, Is.EqualTo(TimeSpan.Zero));
            Assert.That(dt.Value.UtcDateTime, Is.EqualTo(new DateTime(2026, 5, 30, 3, 14, 0, DateTimeKind.Utc)));
        }

        [Test]
        public void Explicit_offset_is_converted_to_UTC() {
            // An offset-bearing DATE-OBS must be normalized to UTC (the same instant), not preserved with its suffix.
            var dt = CaptureScanService.ParseDateObs(Headers("2026-05-30T03:14:00-07:00"));
            Assert.That(dt!.Value.Offset, Is.EqualTo(TimeSpan.Zero));
            Assert.That(dt.Value.UtcDateTime, Is.EqualTo(new DateTime(2026, 5, 30, 10, 14, 0, DateTimeKind.Utc)));
        }

        [Test]
        public void Canonical_O_string_uses_the_Z_free_plus_zero_suffix() {
            // The stored captured_utc string for a UTC DateTimeOffset is the …+00:00 form (never …Z), matching the
            // SqliteFrameRepository write path so the column never holds a mix of suffixes.
            var dt = CaptureScanService.ParseDateObs(Headers("2026-05-30T03:14:00"));
            Assert.That(dt!.Value.ToString("O"), Does.EndWith("+00:00"));
        }

        [Test]
        public void Missing_or_unparseable_DATE_OBS_is_null() {
            Assert.That(CaptureScanService.ParseDateObs(new Dictionary<string, string>()), Is.Null);
            Assert.That(CaptureScanService.ParseDateObs(Headers("not-a-date")), Is.Null);
        }
    }
}
