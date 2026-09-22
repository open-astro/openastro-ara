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
using OpenAstroAra.Astrometry;
using OpenAstroAra.Equipment.Equipment.MyTelescope;
using OpenAstroAra.Fits;
using OpenAstroAra.Server.Services;
using System;
using System.IO;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §29.2 pointing cards — the mount's RA/Dec at readout lands in the FITS header, and the
    /// §18.I header-hint reader can parse what the capture path wrote.
    /// </summary>
    [TestFixture]
    public class CameraServicePointingHeaderTest {

        [Test]
        public void No_mount_yields_no_pointing() {
            Assert.That(CameraService.PointingFrom(null), Is.Null);
        }

        [Test]
        public void Disconnected_mount_yields_no_pointing() {
            var info = new TelescopeInfo { Connected = false };
            info.Coordinates = new Coordinates(Angle.ByHours(1), Angle.ByDegree(2), Epoch.J2000);
            Assert.That(CameraService.PointingFrom(info), Is.Null);
        }

        [Test]
        public void Connected_mount_without_a_position_yet_yields_no_pointing() {
            var info = new TelescopeInfo { Connected = true };
            Assert.That(CameraService.PointingFrom(info), Is.Null);
        }

        [Test]
        public void J2000_mount_position_passes_through_untouched() {
            var info = new TelescopeInfo { Connected = true };
            info.Coordinates = new Coordinates(Angle.ByHours(0.7123), Angle.ByDegree(41.269), Epoch.J2000);
            var pt = CameraService.PointingFrom(info);
            Assert.That(pt, Is.Not.Null);
            Assert.Multiple(() => {
                Assert.That(pt!.Value.RaHours, Is.EqualTo(0.7123).Within(1e-9));
                Assert.That(pt.Value.DecDegrees, Is.EqualTo(41.269).Within(1e-9));
                Assert.That(pt.Value.IsJ2000, Is.True);
            });
        }

        [Test]
        public void JNOW_mount_position_is_recorded_either_transformed_or_flagged_as_native() {
            // With the SOFA native staged this transforms to J2000; without it, it degrades to the
            // mount's own epoch and says so — either way the frame gets a usable pointing.
            var info = new TelescopeInfo { Connected = true };
            info.Coordinates = new Coordinates(Angle.ByHours(0.7123), Angle.ByDegree(41.269), Epoch.JNOW);
            var pt = CameraService.PointingFrom(info);
            Assert.That(pt, Is.Not.Null);
            // Precession 2000 → today is under a degree; a wildly different answer means a bug.
            Assert.Multiple(() => {
                Assert.That(pt!.Value.RaHours, Is.EqualTo(0.7123).Within(0.1));
                Assert.That(pt.Value.DecDegrees, Is.EqualTo(41.269).Within(1.0));
            });
        }

        [Test]
        public void Julian_year_is_2000_at_J2000_epoch_and_advances_by_365_25_days() {
            var j2000 = new DateTimeOffset(2000, 1, 1, 12, 0, 0, TimeSpan.Zero);
            Assert.That(CameraService.JulianYear(j2000), Is.EqualTo(2000.0).Within(1e-9));
            Assert.That(CameraService.JulianYear(j2000.AddDays(365.25)), Is.EqualTo(2001.0).Within(1e-9));
        }

        [Test]
        public void Written_cards_round_trip_through_the_header_hint_reader() {
            var dir = Path.Combine(Path.GetTempPath(), "ara-pointing-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "frame.fits");
            try {
                using (var fits = FitsImage.Create(path, 4, 4, FitsBitDepth.UnsignedShort)) {
                    fits.WriteImageData(new ushort[16]);
                    CameraService.WritePointingHeaders(fits,
                        new CameraService.FramePointing(RaHours: 0.7123, DecDegrees: -41.269, IsJ2000: true),
                        new DateTimeOffset(2026, 9, 22, 4, 0, 0, TimeSpan.Zero));
                    fits.Complete();
                }
                using var read = FitsImage.Open(path);
                var headers = read.ReadHeaders();
                Assert.Multiple(() => {
                    Assert.That(headers.ContainsKey("OBJCTRA"), Is.True);
                    Assert.That(headers.ContainsKey("OBJCTDEC"), Is.True);
                    Assert.That(headers.ContainsKey("RA"), Is.True);
                    Assert.That(headers.ContainsKey("DEC"), Is.True);
                    Assert.That(headers.ContainsKey("EQUINOX"), Is.True);
                });
                var parsed = SqliteFrameRepository.ParseTargetCoordinates(headers["OBJCTRA"], headers["OBJCTDEC"]);
                Assert.That(parsed, Is.Not.Null);
                // Sexagesimal rounds to the second: ~15 arcsec in RA, 1 arcsec in Dec.
                Assert.Multiple(() => {
                    Assert.That(parsed!.Value.RaDegrees, Is.EqualTo(0.7123 * 15).Within(0.01));
                    Assert.That(parsed.Value.DecDegrees, Is.EqualTo(-41.269).Within(0.001));
                });
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void Native_epoch_pointing_writes_the_capture_year_as_equinox_not_2000() {
            var dir = Path.Combine(Path.GetTempPath(), "ara-pointing-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "frame.fits");
            try {
                using (var fits = FitsImage.Create(path, 4, 4, FitsBitDepth.UnsignedShort)) {
                    fits.WriteImageData(new ushort[16]);
                    CameraService.WritePointingHeaders(fits,
                        new CameraService.FramePointing(RaHours: 1, DecDegrees: 1, IsJ2000: false),
                        new DateTimeOffset(2026, 9, 22, 4, 0, 0, TimeSpan.Zero));
                    fits.Complete();
                }
                using var read = FitsImage.Open(path);
                var equinox = double.Parse(read.ReadHeaders()["EQUINOX"], System.Globalization.CultureInfo.InvariantCulture);
                Assert.That(equinox, Is.EqualTo(2026.72).Within(0.01));
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
