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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;

namespace OpenAstroAra.Test {

    /// <summary>
    /// ↑ara#1846 slice 0 — a mount connect fills the Optics Profile Section's empty telescope focal
    /// length and aperture from the mount's reported values (mm), through the store's atomic update,
    /// next to the camera's sensor fill. A stored value is never overwritten; a mount that reports
    /// nothing causes no write.
    /// </summary>
    [TestFixture]
    public class TelescopeServiceOpticsAutoPopulateTest {

        private static TelescopeCapabilitiesDto Caps(double? focalMm, double? apertureMm) =>
            new(CanSlew: true, CanSync: true, CanPark: true, CanUnpark: true,
                CanSetTracking: true, CanPulseGuide: true, CanFindHome: true,
                SupportedSiderealRates: [], FocalLengthMm: focalMm, ApertureDiameterMm: apertureMm);

        private static (TelescopeService Svc, InMemoryProfileStore Store, Func<int> Writes) Rig(OpticsSettingsDto optics) {
            var store = new InMemoryProfileStore();
            store.PutOpticsSettings(optics);
            var writes = 0;
            store.Changed += (_, _) => writes++; // subscribed after the seed: counts only the fill's writes
            return (new TelescopeService(profileStore: store), store, () => writes);
        }

        private static OpticsSettingsDto Optics(double focal = 0, double aperture = 0) =>
            new(FocalLengthMm: focal, ReducerFactor: 1.0, SensorWidthPx: 6248, SensorHeightPx: 4176,
                PixelSizeUm: 3.76, ApertureMm: aperture, TelescopeName: "Test scope");

        [Test]
        public void Connect_with_empty_optics_fills_focal_length_and_aperture_from_the_mount() {
            var (svc, store, writes) = Rig(Optics());
            using var _ = svc;
            svc.MaybeAutoPopulateOptics(Caps(480, 80));
            var optics = store.GetOpticsSettings();
            Assert.That(optics.FocalLengthMm, Is.EqualTo(480));
            Assert.That(optics.ApertureMm, Is.EqualTo(80));
            Assert.That(optics.SensorWidthPx, Is.EqualTo(6248), "the camera's fields are untouched");
            Assert.That(optics.TelescopeName, Is.EqualTo("Test scope"));
            Assert.That(writes(), Is.EqualTo(1));
        }

        [Test]
        public void Existing_values_are_left_alone_and_only_the_empty_field_fills() {
            var (svc, store, writes) = Rig(Optics(focal: 1000, aperture: 0));
            using var _ = svc;
            svc.MaybeAutoPopulateOptics(Caps(480, 80));
            Assert.That(store.GetOpticsSettings().FocalLengthMm, Is.EqualTo(1000), "a stored focal length wins");
            Assert.That(store.GetOpticsSettings().ApertureMm, Is.EqualTo(80), "the empty aperture fills");

            var (svc2, store2, writes2) = Rig(Optics(focal: 1000, aperture: 200));
            using var _2 = svc2;
            svc2.MaybeAutoPopulateOptics(Caps(480, 80));
            Assert.That(store2.GetOpticsSettings(), Is.EqualTo(Optics(1000, 200)));
            Assert.That(writes2(), Is.EqualTo(0), "nothing empty, nothing written");
            Assert.That(writes(), Is.EqualTo(1));
        }

        [TestCase(null, null)]
        [TestCase(0.0, 0.0)]
        public void A_mount_reporting_no_optics_writes_nothing(double? focal, double? aperture) {
            var (svc, store, writes) = Rig(Optics());
            using var _ = svc;
            svc.MaybeAutoPopulateOptics(Caps(focal, aperture));
            Assert.That(store.GetOpticsSettings(), Is.EqualTo(Optics()));
            Assert.That(writes(), Is.EqualTo(0));
        }

        [Test]
        public void A_throwing_property_reads_as_null_and_writes_nothing() {
            // ReadCapabilities maps a throwing FocalLength / ApertureDiameter to null via OpticsMmFromMetres' try.
            Assert.That(TelescopeService.OpticsMmFromMetres(0.48), Is.EqualTo(480).Within(1e-9));
            var (svc, store, writes) = Rig(Optics());
            using var _ = svc;
            svc.MaybeAutoPopulateOptics(Caps(null, null));
            Assert.That(writes(), Is.EqualTo(0));
            Assert.That(store.GetOpticsSettings(), Is.EqualTo(Optics()));
        }
    }
}