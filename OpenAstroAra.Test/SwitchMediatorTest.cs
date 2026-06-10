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
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Services;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// Sim-free unit coverage for the §14e <see cref="ISwitchMediator"/> surface that
    /// <see cref="SwitchService"/> serves alongside its REST surface (one singleton backs both, so
    /// the <c>SetSwitchValue</c> instruction drives the live switch hub). The live write path is
    /// exercised by the <c>[Category("Integration")]</c> companion test; here we cover the
    /// not-connected / disposed contracts plus the pure index-mapping and collection-building
    /// helpers against fabricated port snapshots.
    /// </summary>
    [TestFixture]
    public class SwitchMediatorTest {

        private static SwitchPortSnapshot[] SampleSnapshots() => new[] {
            new SwitchPortSnapshot(Id: 0, Name: "Mount power", Description: "12V relay",
                Value: 1, Min: 0, Max: 1, StepSize: 1, CanWrite: true),
            new SwitchPortSnapshot(Id: 1, Name: "Ambient light", Description: "lux sensor",
                Value: 42, Min: 0, Max: 100000, StepSize: 0.1, CanWrite: false),
            new SwitchPortSnapshot(Id: 2, Name: "Dew heater", Description: "PWM 0-100",
                Value: 30, Min: 0, Max: 100, StepSize: 5, CanWrite: true),
        };

        [Test]
        public void GetInfo_before_connect_reports_not_connected_with_empty_collections() {
            using var svc = new SwitchService();
            var info = ((ISwitchMediator)svc).GetInfo();
            Assert.That(info.Connected, Is.False);
            Assert.That(info.WritableSwitches, Is.Empty);
            Assert.That(info.ReadonlySwitches, Is.Empty);
        }

        [Test]
        public void GetInfo_after_Dispose_reports_not_connected_without_throwing() {
            var svc = new SwitchService();
            svc.Dispose();
            var info = ((ISwitchMediator)svc).GetInfo();
            Assert.That(info.Connected, Is.False);
            Assert.That(info.WritableSwitches, Is.Empty);
        }

        [Test]
        public void SetSwitchValue_when_not_connected_completes_without_throwing() {
            using var svc = new SwitchService();
            Assert.DoesNotThrowAsync(() =>
                ((ISwitchMediator)svc).SetSwitchValue(0, 1.0, progress: null!, CancellationToken.None));
        }

        [Test]
        public void SetSwitchValue_after_Dispose_completes_without_throwing() {
            var svc = new SwitchService();
            svc.Dispose();
            Assert.DoesNotThrowAsync(() =>
                ((ISwitchMediator)svc).SetSwitchValue(0, 1.0, progress: null!, CancellationToken.None));
        }

        [Test]
        public void BuildSwitchCollections_splits_by_CanWrite_preserving_cache_order() {
            using var svc = new SwitchService();
            var (writable, readonlySwitches) = SwitchService.BuildSwitchCollections(svc, SampleSnapshots());

            Assert.That(writable, Has.Count.EqualTo(2));
            Assert.That(readonlySwitches, Has.Count.EqualTo(1));
            Assert.That(writable[0].Id, Is.EqualTo(0));
            Assert.That(writable[1].Id, Is.EqualTo(2));
            Assert.That(readonlySwitches[0].Id, Is.EqualTo(1));
        }

        [Test]
        public void BuildSwitchCollections_wrappers_surface_range_step_and_metadata() {
            using var svc = new SwitchService();
            var (writable, _) = SwitchService.BuildSwitchCollections(svc, SampleSnapshots());

            var dewHeater = writable[1];
            Assert.That(dewHeater.Name, Is.EqualTo("Dew heater"));
            Assert.That(dewHeater.Description, Is.EqualTo("PWM 0-100"));
            Assert.That(dewHeater.Minimum, Is.EqualTo(0));
            Assert.That(dewHeater.Maximum, Is.EqualTo(100));
            Assert.That(dewHeater.StepSize, Is.EqualTo(5));
            // TargetValue seeds from the snapshot value and is settable (UI/SetValue contract).
            Assert.That(dewHeater.TargetValue, Is.EqualTo(30));
            dewHeater.TargetValue = 55;
            Assert.That(dewHeater.TargetValue, Is.EqualTo(55));
        }

        [Test]
        public void Wrapper_Value_reads_live_cache_and_is_NaN_when_not_connected() {
            using var svc = new SwitchService();
            var (writable, _) = SwitchService.BuildSwitchCollections(svc, SampleSnapshots());
            // The service has no connected device, so the live cache lookup reports "unknown".
            Assert.That(writable[0].Value, Is.NaN);
        }

        [Test]
        public void Wrapper_Poll_when_not_connected_reports_false_without_throwing() {
            using var svc = new SwitchService();
            var (writable, _) = SwitchService.BuildSwitchCollections(svc, SampleSnapshots());
            Assert.That(writable[0].Poll(), Is.False);
        }

        [Test]
        public void WritablePortIdAt_maps_collection_index_to_port_id_skipping_readonly_ports() {
            var snapshots = SampleSnapshots();
            Assert.That(SwitchService.WritablePortIdAt(snapshots, 0), Is.EqualTo((short)0));
            Assert.That(SwitchService.WritablePortIdAt(snapshots, 1), Is.EqualTo((short)2));
        }

        [Test]
        public void WritablePortIdAt_rejects_out_of_range_indices() {
            var snapshots = SampleSnapshots();
            Assert.That(SwitchService.WritablePortIdAt(snapshots, -1), Is.Null);
            Assert.That(SwitchService.WritablePortIdAt(snapshots, 2), Is.Null);
            Assert.That(SwitchService.WritablePortIdAt(System.Array.Empty<SwitchPortSnapshot>(), 0), Is.Null);
        }
    }
}
