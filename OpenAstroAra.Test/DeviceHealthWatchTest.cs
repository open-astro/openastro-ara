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
using System.Collections.Generic;

namespace OpenAstroAra.Test {

    /// <summary>The per-service half of the §42.3 second channel: one fault per episode, recovered on the
    /// first good read, and resolved when the session ends so the §42.5 row never seeds the chip again.</summary>
    [TestFixture]
    public class DeviceHealthWatchTest {

        private sealed class RecordingSink : IEquipmentFaultSink {
            public readonly List<EquipmentFaultEvent> Published = new();
            public readonly List<EquipmentFaultEvent> Recovered = new();
            public void Publish(EquipmentFaultEvent fault) => Published.Add(fault);
            void IEquipmentFaultSink.Recovered(EquipmentFaultEvent fault) => Recovered.Add(fault);
        }

        private static readonly DiscoveredDeviceDto Wheel = new("efw", "ZWO EFW", DeviceType.FilterWheel, "localhost", "127.0.0.1", 6800, 0, false);

        [Test]
        public void Three_failures_publish_one_degraded_fault_and_the_first_good_read_recovers_it() {
            var sink = new RecordingSink();
            var watch = new DeviceHealthWatch(DeviceType.FilterWheel, sink);
            for (var i = 0; i < 5; i++) {
                watch.Observe(false, "EFW_ERROR_REMOVED", Wheel);
            }
            Assert.That(sink.Published, Has.Count.EqualTo(1));
            Assert.That(sink.Published[0].Kind, Is.EqualTo(EquipmentFaultKind.Degraded));
            Assert.That(sink.Published[0].DeviceName, Is.EqualTo("ZWO EFW"));
            Assert.That(sink.Published[0].Details, Does.Contain("EFW_ERROR_REMOVED"));

            watch.Observe(true, null, Wheel);
            Assert.That(sink.Recovered, Has.Count.EqualTo(1));
            Assert.That(sink.Recovered[0], Is.SameAs(sink.Published[0]), "the same fault is resolved");
        }

        [Test]
        public void Reset_resolves_a_standing_episode_so_its_row_cannot_seed_the_chip_again() {
            // Review on #1384: a disconnect / reconnect / trip reset the tracker but left the degraded
            // row unresolved, and the next relaunch seeded it as a standing amber for up to a day.
            var sink = new RecordingSink();
            var watch = new DeviceHealthWatch(DeviceType.Camera, sink);
            for (var i = 0; i < 3; i++) {
                watch.Observe(false, "ASI_ERROR_GENERAL_ERROR", Wheel);
            }
            Assert.That(sink.Published, Has.Count.EqualTo(1));

            watch.Reset();
            Assert.That(sink.Recovered, Has.Count.EqualTo(1), "the session ended: the episode is resolved");
            watch.Reset();
            Assert.That(sink.Recovered, Has.Count.EqualTo(1), "nothing standing: nothing to resolve");

            // A fresh session gets its own episode.
            for (var i = 0; i < 3; i++) {
                watch.Observe(false, "ASI_ERROR_GENERAL_ERROR", Wheel);
            }
            Assert.That(sink.Published, Has.Count.EqualTo(2));
        }

        [Test]
        public void A_blip_publishes_nothing_and_a_watch_without_a_sink_is_inert() {
            var sink = new RecordingSink();
            var watch = new DeviceHealthWatch(DeviceType.Rotator, sink);
            watch.Observe(false, "x", Wheel);
            watch.Observe(false, "x", Wheel);
            watch.Observe(true, null, Wheel);
            Assert.That(sink.Published, Is.Empty);
            Assert.That(sink.Recovered, Is.Empty);
            var silent = new DeviceHealthWatch(DeviceType.Rotator, faults: null);
            for (var i = 0; i < 4; i++) {
                silent.Observe(false, "x", Wheel);
            }
            silent.Reset();
            Assert.Pass("no sink, no throw");
        }
    }
}
