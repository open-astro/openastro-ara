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

namespace OpenAstroAra.Test {

    /// <summary>§31.4 — the pure NMEA parsing the USB-GPS self-sync slice will wire up:
    /// RMC/GGA happy paths, the void-fix and checksum rejections, and hemisphere signs.</summary>
    [TestFixture]
    public class NmeaSentenceParserTest {

        // Reference sentence (checksum-correct): 2026-07-11 06:15:30 UTC at 30°16.2'N 97°44.4'W.
        private const string Rmc = "$GPRMC,061530.00,A,3016.20,N,09744.40,W,0.0,0.0,110726,,,A*6A";

        private static string WithChecksum(string bodyWithDollar) {
            byte sum = 0;
            foreach (var c in bodyWithDollar[1..]) {
                sum ^= (byte)c;
            }
            return $"{bodyWithDollar}*{sum:X2}";
        }

        [Test]
        public void Rmc_active_fix_parses_time_and_position() {
            var fix = NmeaSentenceParser.Parse(WithChecksum("$GPRMC,061530.00,A,3016.20,N,09744.40,W,0.0,0.0,110726,,,A"));
            Assert.That(fix, Is.Not.Null);
            Assert.That(fix!.TimeUtc, Is.EqualTo(new DateTimeOffset(2026, 7, 11, 6, 15, 30, TimeSpan.Zero)));
            Assert.That(fix.LatitudeDeg, Is.EqualTo(30.27).Within(1e-6));
            Assert.That(fix.LongitudeDeg, Is.EqualTo(-97.74).Within(1e-6));
            Assert.That(fix.AltitudeM, Is.Null, "RMC carries no altitude");
        }

        [Test]
        public void Rmc_void_fix_is_rejected() {
            var fix = NmeaSentenceParser.Parse(WithChecksum("$GPRMC,061530.00,V,3016.20,N,09744.40,W,0.0,0.0,110726,,,N"));
            Assert.That(fix, Is.Null, "V = void: the receiver has time but no trusted fix");
        }

        [Test]
        public void A_corrupted_checksum_is_rejected() {
            var good = WithChecksum("$GPRMC,061530.00,A,3016.20,N,09744.40,W,0.0,0.0,110726,,,A");
            var bad = good[..^2] + "00";
            Assert.That(NmeaSentenceParser.Parse(good), Is.Not.Null);
            Assert.That(NmeaSentenceParser.Parse(bad), Is.Null, "serial line noise must never parse");
        }

        [Test]
        public void Gga_carries_position_and_altitude_but_no_instant() {
            var fix = NmeaSentenceParser.Parse(WithChecksum("$GPGGA,061530.00,3016.20,N,09744.40,W,1,08,1.0,165.0,M,0.0,M,,"));
            Assert.That(fix, Is.Not.Null);
            Assert.That(fix!.TimeUtc, Is.Null, "GGA has no date field — RMC supplies the sync instant");
            Assert.That(fix.LatitudeDeg, Is.EqualTo(30.27).Within(1e-6));
            Assert.That(fix.AltitudeM, Is.EqualTo(165.0));
        }

        [Test]
        public void Gga_without_a_fix_is_rejected() {
            var fix = NmeaSentenceParser.Parse(WithChecksum("$GPGGA,061530.00,,,,,0,00,,,M,,M,,"));
            Assert.That(fix, Is.Null, "quality 0 = searching, not a fix");
        }

        [Test]
        public void Multi_constellation_talker_parses_the_same() {
            var fix = NmeaSentenceParser.Parse(WithChecksum("$GNRMC,061530.00,A,3016.20,N,09744.40,W,0.0,0.0,110726,,,A"));
            Assert.That(fix, Is.Not.Null, "$GNxxx from a GPS+GLONASS receiver is the same sentence");
        }

        [Test]
        public void Southern_and_eastern_hemispheres_sign_correctly() {
            var fix = NmeaSentenceParser.Parse(WithChecksum("$GPRMC,061530.00,A,3352.80,S,15112.60,E,0.0,0.0,110726,,,A"));
            Assert.That(fix, Is.Not.Null);
            Assert.That(fix!.LatitudeDeg, Is.EqualTo(-33.88).Within(1e-6));
            Assert.That(fix.LongitudeDeg, Is.EqualTo(151.21).Within(1e-6));
        }

        [Test]
        public void Garbage_and_unrelated_sentences_parse_to_null() {
            Assert.That(NmeaSentenceParser.Parse(null), Is.Null);
            Assert.That(NmeaSentenceParser.Parse(""), Is.Null);
            Assert.That(NmeaSentenceParser.Parse("not nmea at all"), Is.Null);
            Assert.That(NmeaSentenceParser.Parse("$GPGSV,3,1,11,,,,,,,,,,,,,,,,*45"), Is.Null, "satellite-view sentences are ignored");
            Assert.That(NmeaSentenceParser.Parse(Rmc[..20]), Is.Null, "a truncated sentence never parses");
        }
    }
}
