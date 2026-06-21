#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §52.1 <see cref="EquipmentSelectionStore"/> — the per-type "last connected device"
    /// memory that lets auto-connect-on-boot know which device to re-establish. Covers the
    /// remember/read round-trip, per-type upsert, empty-when-absent, and corrupt-file tolerance.
    /// </summary>
    [TestFixture]
    public class EquipmentSelectionStoreTest {

        private string _dir = null!;
        private EquipmentSelectionStore _store = null!;

        [SetUp]
        public void SetUp() {
            _dir = Path.Combine(Path.GetTempPath(), "ara-eqsel-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_dir);
            _store = new EquipmentSelectionStore(_dir, NullLogger<EquipmentSelectionStore>.Instance);
        }

        [TearDown]
        public void TearDown() {
            _store.Dispose();
            if (Directory.Exists(_dir)) {
                Directory.Delete(_dir, recursive: true);
            }
        }

        private static DiscoveredDeviceDto Device(DeviceType type, string id, string name) =>
            new(UniqueId: id, Name: name, Type: type, HostName: "host", IpAddress: "127.0.0.1",
                IpPort: 11111, AlpacaDeviceNumber: 0, UseHttps: false);

        [Test]
        public async Task GetAll_is_empty_when_nothing_remembered() {
            var all = await _store.GetAllAsync(CancellationToken.None);
            Assert.That(all, Is.Empty);
        }

        [Test]
        public async Task Remember_then_GetAll_round_trips_the_device() {
            var cam = Device(DeviceType.Camera, "cam-1", "ASI2600");
            await _store.RememberAsync(cam, CancellationToken.None);

            var all = await _store.GetAllAsync(CancellationToken.None);
            Assert.That(all.ContainsKey(DeviceType.Camera), Is.True);
            Assert.That(all[DeviceType.Camera].UniqueId, Is.EqualTo("cam-1"));
            Assert.That(all[DeviceType.Camera].Name, Is.EqualTo("ASI2600"));
            Assert.That(all[DeviceType.Camera].Type, Is.EqualTo(DeviceType.Camera));
        }

        [Test]
        public async Task Remember_survives_a_fresh_store_instance() {
            await _store.RememberAsync(Device(DeviceType.Telescope, "mnt-1", "EQ6"), CancellationToken.None);

            // A new instance over the same dir reads the persisted file (simulates a daemon restart).
            var reopened = new EquipmentSelectionStore(_dir, NullLogger<EquipmentSelectionStore>.Instance);
            try {
                var all = await reopened.GetAllAsync(CancellationToken.None);
                Assert.That(all[DeviceType.Telescope].UniqueId, Is.EqualTo("mnt-1"));
            } finally {
                reopened.Dispose();
            }
        }

        [Test]
        public async Task Remember_upserts_per_type_keeping_other_types() {
            await _store.RememberAsync(Device(DeviceType.Camera, "cam-1", "old"), CancellationToken.None);
            await _store.RememberAsync(Device(DeviceType.Focuser, "foc-1", "EAF"), CancellationToken.None);
            // Re-remembering the same type replaces only that entry.
            await _store.RememberAsync(Device(DeviceType.Camera, "cam-2", "new"), CancellationToken.None);

            var all = await _store.GetAllAsync(CancellationToken.None);
            Assert.That(all[DeviceType.Camera].UniqueId, Is.EqualTo("cam-2"));
            Assert.That(all[DeviceType.Focuser].UniqueId, Is.EqualTo("foc-1"));
            Assert.That(all.Count, Is.EqualTo(2));
        }

        [Test]
        public async Task GetAll_tolerates_a_corrupt_file() {
            await File.WriteAllTextAsync(Path.Combine(_dir, EquipmentSelectionStore.FileName), "{ not json");
            var all = await _store.GetAllAsync(CancellationToken.None);
            Assert.That(all, Is.Empty);
        }
    }
}
