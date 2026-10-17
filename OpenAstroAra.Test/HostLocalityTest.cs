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
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>A device on this machine's own Alpaca server is dialled and remembered as localhost.</summary>
    [TestFixture]
    public class HostLocalityTest {

        private static readonly IPAddress[] Nics = [IPAddress.Parse("172.24.1.1"), IPAddress.Parse("192.168.8.118"), IPAddress.Parse("fe80::1234%2")];

        private static bool ThisBox(string? host) => HostLocality.IsLocalHost(host, "openastro", Nics);

        private static DiscoveredDeviceDto Device(string host, string ip) =>
            new(UniqueId: "cam", Name: "ASI2600MM", Type: DeviceType.Camera, HostName: host, IpAddress: ip,
                IpPort: 6800, AlpacaDeviceNumber: 1, UseHttps: false);

        [TestCase("172.24.1.1", ExpectedResult = true)]      // the hotspot
        [TestCase("192.168.8.118", ExpectedResult = true)]   // the field router
        [TestCase("127.0.0.1", ExpectedResult = true)]
        [TestCase("localhost", ExpectedResult = true)]
        [TestCase("openastro", ExpectedResult = true)]
        [TestCase("OPENASTRO.local", ExpectedResult = true)]
        [TestCase("[fe80::1234]", ExpectedResult = true)]
        [TestCase("192.168.1.235", ExpectedResult = false)]  // another box
        [TestCase("openastro.site2", ExpectedResult = false)]
        [TestCase("rc91.lan", ExpectedResult = false)]
        public bool Is_local_host_knows_this_machines_names_and_addresses(string host) => ThisBox(host);

        [Test]
        public void A_device_on_one_of_this_machines_addresses_becomes_localhost() {
            var local = HostLocality.LocalizeDevice(Device("192.168.8.118", "192.168.8.118"), ThisBox);
            Assert.That(local.HostName, Is.EqualTo("localhost"));
            Assert.That(local.IpAddress, Is.EqualTo("127.0.0.1"));
            Assert.That(local.IpPort, Is.EqualTo(6800), "only the address changes");
            Assert.That(local.UniqueId, Is.EqualTo("cam"));

            var hotspot = HostLocality.LocalizeDevice(Device("openastro.lan", "172.24.1.1"), ThisBox);
            Assert.That(hotspot.HostName, Is.EqualTo("localhost"));
        }

        [Test]
        public void Either_field_naming_this_machine_is_enough() {
            Assert.That(HostLocality.LocalizeDevice(Device("", "172.24.1.1"), ThisBox).HostName, Is.EqualTo("localhost"), "ip only");
            Assert.That(HostLocality.LocalizeDevice(Device("openastro", ""), ThisBox).IpAddress, Is.EqualTo("127.0.0.1"), "name only");
        }

        [Test]
        public void A_device_on_another_machine_is_untouched() {
            var remote = Device("rc91.lan", "192.168.1.235");
            Assert.That(HostLocality.LocalizeDevice(remote, ThisBox), Is.SameAs(remote));
        }

        [Test]
        public void An_already_local_record_is_returned_as_is() {
            var local = Device("localhost", "127.0.0.1");
            Assert.That(HostLocality.LocalizeDevice(local, ThisBox), Is.SameAs(local));
        }
        // The home LAN: 192.168.8.118 was the field router's address and is on no interface any more.
        private static readonly IPAddress[] HomeNics = [IPAddress.Parse("192.168.1.118"), IPAddress.Parse("172.24.1.1")];

        private static bool AtHome(string? host) => HostLocality.IsLocalHost(host, "openastro", HomeNics);

        // configureddevices per host; a host with no entry answers nothing (unreachable).
        private sealed class FakeAlpaca(Dictionary<string, Dictionary<string, AlpacaConfiguredDevice>> servers) : IAlpacaManagementClient {
            public List<string> Asked { get; } = [];
            public Task<IReadOnlyDictionary<string, string>> GetConfiguredDeviceNamesAsync(string host, int port, CancellationToken ct) =>
                throw new System.NotSupportedException();
            public Task<IReadOnlyDictionary<string, AlpacaConfiguredDevice>> GetConfiguredDevicesAsync(string host, int port, CancellationToken ct) {
                lock (Asked) {
                    Asked.Add(host);
                }
                return Task.FromResult<IReadOnlyDictionary<string, AlpacaConfiguredDevice>>(
                    servers.TryGetValue(host, out var d) ? d : new Dictionary<string, AlpacaConfiguredDevice>());
            }
        }

        private static DiscoveredDeviceDto Focuser(string ip, string id = "ZWO_EAF_0", string name = "ZWO EAF") =>
            new(UniqueId: id, Name: name, Type: DeviceType.Focuser, HostName: ip, IpAddress: ip,
                IpPort: 6800, AlpacaDeviceNumber: 0, UseHttps: false);

        private static Dictionary<string, AlpacaConfiguredDevice> LocalBridge(string id = "ZWO_EAF_0", string name = "ZWO EAF") =>
            new() { ["focuser/0"] = new AlpacaConfiguredDevice(name, id) };

        [Test]
        public async Task A_device_saved_under_an_address_this_box_lost_is_found_on_the_local_server() {
            var alpaca = new FakeAlpaca(new() { ["127.0.0.1"] = LocalBridge() });

            var moved = await HostLocality.FindOnLocalServerAsync(Focuser("192.168.8.118"), alpaca, CancellationToken.None, AtHome);

            Assert.That(moved, Is.Not.Null);
            Assert.That(moved!.HostName, Is.EqualTo("localhost"));
            Assert.That(moved.IpAddress, Is.EqualTo("127.0.0.1"));
            Assert.That(moved.IpPort, Is.EqualTo(6800));
            Assert.That(alpaca.Asked, Has.Count.EqualTo(2).And.Contains("192.168.8.118").And.Contains("127.0.0.1"));
        }

        [Test]
        public async Task A_bridge_that_renamed_its_unique_id_still_matches_by_name() {
            // AlpacaBridge 5 renamed the ZWO dew heater's id; the saved record carries the old one.
            var alpaca = new FakeAlpaca(new() { ["127.0.0.1"] = LocalBridge(id: "ZWO_DEW_1", name: "ZWO EAF") });

            var moved = await HostLocality.FindOnLocalServerAsync(Focuser("192.168.8.118", id: "ZWO_DEW_SN_2e18"), alpaca, CancellationToken.None, AtHome);

            Assert.That(moved?.UniqueId, Is.EqualTo("ZWO_DEW_1"), "the local server's id is carried forward");
        }

        [Test]
        public async Task A_saved_server_that_still_serves_the_device_keeps_it() {
            // Another box on the LAN that is up: its focuser is not ours, even with the same id and number.
            var alpaca = new FakeAlpaca(new() { ["192.168.1.235"] = LocalBridge(), ["127.0.0.1"] = LocalBridge() });

            Assert.That(await HostLocality.FindOnLocalServerAsync(Focuser("192.168.1.235"), alpaca, CancellationToken.None, AtHome), Is.Null);
        }

        [Test]
        public async Task A_different_device_at_the_same_number_on_the_local_server_is_not_taken() {
            var alpaca = new FakeAlpaca(new() { ["127.0.0.1"] = LocalBridge(id: "PEGASUS_FOC_0", name: "Pegasus FocusCube") });

            Assert.That(await HostLocality.FindOnLocalServerAsync(Focuser("192.168.8.118"), alpaca, CancellationToken.None, AtHome), Is.Null);
        }

        [Test]
        public async Task A_local_record_asks_no_server() {
            var alpaca = new FakeAlpaca(new());

            Assert.That(await HostLocality.FindOnLocalServerAsync(Focuser("localhost"), alpaca, CancellationToken.None, AtHome), Is.Null);
            Assert.That(await HostLocality.FindOnLocalServerAsync(Focuser("192.168.1.118"), alpaca, CancellationToken.None, AtHome), Is.Null,
                "an address this box has is LocalizeDevice's job");
            Assert.That(alpaca.Asked, Is.Empty);
        }
    }
}
