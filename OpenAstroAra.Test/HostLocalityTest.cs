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
using System.Net;

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
    }
}
