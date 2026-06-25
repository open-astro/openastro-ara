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
using System.Linq;
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

        private static DiscoveredDeviceDto Device(DeviceType type, string id, string name, int number = 0) =>
            new(UniqueId: id, Name: name, Type: type, HostName: "host", IpAddress: "127.0.0.1",
                IpPort: 11111, AlpacaDeviceNumber: number, UseHttps: false);

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
            var found = all.Single(d => d.Type == DeviceType.Camera);
            Assert.That(found.UniqueId, Is.EqualTo("cam-1"));
            Assert.That(found.Name, Is.EqualTo("ASI2600"));
        }

        [Test]
        public async Task Remember_survives_a_fresh_store_instance() {
            await _store.RememberAsync(Device(DeviceType.Telescope, "mnt-1", "EQ6"), CancellationToken.None);

            // A new instance over the same dir reads the persisted file (simulates a daemon restart).
            var reopened = new EquipmentSelectionStore(_dir, NullLogger<EquipmentSelectionStore>.Instance);
            try {
                var all = await reopened.GetAllAsync(CancellationToken.None);
                Assert.That(all.Single(d => d.Type == DeviceType.Telescope).UniqueId, Is.EqualTo("mnt-1"));
            } finally {
                reopened.Dispose();
            }
        }

        [Test]
        public async Task Remember_upserts_per_type_keeping_other_types() {
            await _store.RememberAsync(Device(DeviceType.Camera, "cam-1", "old"), CancellationToken.None);
            await _store.RememberAsync(Device(DeviceType.Focuser, "foc-1", "EAF"), CancellationToken.None);
            // Re-remembering the same single-instance type replaces only that entry.
            await _store.RememberAsync(Device(DeviceType.Camera, "cam-2", "new"), CancellationToken.None);

            var all = await _store.GetAllAsync(CancellationToken.None);
            Assert.That(all.Single(d => d.Type == DeviceType.Camera).UniqueId, Is.EqualTo("cam-2"));
            Assert.That(all.Single(d => d.Type == DeviceType.Focuser).UniqueId, Is.EqualTo("foc-1"));
            Assert.That(all.Count, Is.EqualTo(2));
        }

        [Test]
        public async Task Remember_keeps_multiple_switches_independently() {
            // Switch is multi-instance: each switch (by Alpaca device number) is remembered separately.
            await _store.RememberAsync(Device(DeviceType.Switch, "sw-a", "PowerBox", number: 0), CancellationToken.None);
            await _store.RememberAsync(Device(DeviceType.Switch, "sw-b", "RelayBox", number: 1), CancellationToken.None);

            var all = await _store.GetAllAsync(CancellationToken.None);
            var ids = all.Where(d => d.Type == DeviceType.Switch).Select(s => s.UniqueId).ToList();
            Assert.That(ids.Count, Is.EqualTo(2));
            Assert.That(ids, Does.Contain("sw-a"));
            Assert.That(ids, Does.Contain("sw-b"));
        }

        [Test]
        public async Task Remember_upserts_a_switch_by_device_number() {
            await _store.RememberAsync(Device(DeviceType.Switch, "sw-a", "old", number: 0), CancellationToken.None);
            await _store.RememberAsync(Device(DeviceType.Switch, "sw-b", "other", number: 1), CancellationToken.None);
            // Re-remembering device number 0 replaces only that switch, not number 1.
            await _store.RememberAsync(Device(DeviceType.Switch, "sw-a2", "new", number: 0), CancellationToken.None);

            var switches = (await _store.GetAllAsync(CancellationToken.None))
                .Where(d => d.Type == DeviceType.Switch).ToList();
            Assert.That(switches.Count, Is.EqualTo(2));
            Assert.That(switches.Single(s => s.AlpacaDeviceNumber == 0).UniqueId, Is.EqualTo("sw-a2"));
            Assert.That(switches.Single(s => s.AlpacaDeviceNumber == 1).UniqueId, Is.EqualTo("sw-b"));
        }

        [Test]
        public async Task Remember_switch_drops_a_legacy_bare_switch_entry() {
            // A pre-multi-switch file remembered the single switch under the bare "Switch" key.
            await File.WriteAllTextAsync(Path.Combine(_dir, EquipmentSelectionStore.FileName),
                "{ \"Switch\": { \"uniqueId\": \"legacy\", \"name\": \"old\", \"type\": \"Switch\", " +
                "\"hostName\": \"host\", \"ipAddress\": \"127.0.0.1\", \"ipPort\": 11111, " +
                "\"alpacaDeviceNumber\": 0, \"useHttps\": false } }");

            // Remembering a numbered switch must not double up with the legacy bare entry.
            await _store.RememberAsync(Device(DeviceType.Switch, "sw-new", "new", number: 0), CancellationToken.None);

            var switches = (await _store.GetAllAsync(CancellationToken.None))
                .Where(d => d.Type == DeviceType.Switch).ToList();
            Assert.That(switches.Count, Is.EqualTo(1));
            Assert.That(switches[0].UniqueId, Is.EqualTo("sw-new"));
        }

        [Test]
        public async Task GetAll_collapses_a_legacy_and_canonical_switch_key_for_the_same_device() {
            // First boot after upgrade: a file can hold BOTH the legacy bare "Switch" and a new
            // "Switch:0" for the same switch. GetAll must return one (else auto-connect doubles up).
            await File.WriteAllTextAsync(Path.Combine(_dir, EquipmentSelectionStore.FileName),
                "{ \"Switch\": { \"UniqueId\": \"legacy\", \"Name\": \"old\", \"Type\": \"Switch\", " +
                "\"HostName\": \"host\", \"IpAddress\": \"127.0.0.1\", \"IpPort\": 11111, " +
                "\"AlpacaDeviceNumber\": 0, \"UseHttps\": false }, " +
                "\"Switch:0\": { \"UniqueId\": \"current\", \"Name\": \"new\", \"Type\": \"Switch\", " +
                "\"HostName\": \"host\", \"IpAddress\": \"127.0.0.1\", \"IpPort\": 11111, " +
                "\"AlpacaDeviceNumber\": 0, \"UseHttps\": false } }");

            var switches = (await _store.GetAllAsync(CancellationToken.None))
                .Where(d => d.Type == DeviceType.Switch).ToList();
            Assert.That(switches.Count, Is.EqualTo(1));
            Assert.That(switches[0].UniqueId, Is.EqualTo("current")); // canonical "Switch:0" wins
        }

        [Test]
        public async Task GetAll_collapse_prefers_the_canonical_key_regardless_of_file_order() {
            // Same device as the prior test but with the canonical "Switch:0" listed BEFORE the
            // legacy bare "Switch" in the file (the reverse order). The canonical entry must still
            // win — the de-dup can't depend on dictionary iteration order.
            await File.WriteAllTextAsync(Path.Combine(_dir, EquipmentSelectionStore.FileName),
                "{ \"Switch:0\": { \"UniqueId\": \"current\", \"Name\": \"new\", \"Type\": \"Switch\", " +
                "\"HostName\": \"host\", \"IpAddress\": \"127.0.0.1\", \"IpPort\": 11111, " +
                "\"AlpacaDeviceNumber\": 0, \"UseHttps\": false }, " +
                "\"Switch\": { \"UniqueId\": \"legacy\", \"Name\": \"old\", \"Type\": \"Switch\", " +
                "\"HostName\": \"host\", \"IpAddress\": \"127.0.0.1\", \"IpPort\": 11111, " +
                "\"AlpacaDeviceNumber\": 0, \"UseHttps\": false } }");

            var switches = (await _store.GetAllAsync(CancellationToken.None))
                .Where(d => d.Type == DeviceType.Switch).ToList();
            Assert.That(switches.Count, Is.EqualTo(1));
            Assert.That(switches[0].UniqueId, Is.EqualTo("current"));
        }

        [Test]
        public async Task GetAll_tolerates_a_corrupt_file() {
            await File.WriteAllTextAsync(Path.Combine(_dir, EquipmentSelectionStore.FileName), "{ not json");
            var all = await _store.GetAllAsync(CancellationToken.None);
            Assert.That(all, Is.Empty);
        }
    }
}
