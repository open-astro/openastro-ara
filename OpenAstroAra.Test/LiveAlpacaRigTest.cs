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
using System.Collections.Generic;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #1134 — the real-gear opt-in of the live fixtures (<see cref="LiveAlpacaRig"/>), read from a
    /// fake environment: no URL keeps the OmniSim path, motion stays off unless asked for, bad values
    /// fail loudly, and the device pick follows the per-type number.
    /// </summary>
    [TestFixture]
    public class LiveAlpacaRigTest {

        private static Func<string, string?> FakeEnv(params (string Name, string Value)[] vars) {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, value) in vars) {
                map[name] = value;
            }
            return name => map.TryGetValue(name, out var v) ? v : null;
        }

        private static readonly IReadOnlyDictionary<string, AlpacaConfiguredDevice> Configured =
            new Dictionary<string, AlpacaConfiguredDevice>(StringComparer.Ordinal) {
                ["camera/1"] = new("ZWO ASI2600MC Pro", "cam-1"),
                ["camera/0"] = new("ZWO ASI120MM Mini", "cam-0"),
                ["telescope/0"] = new("HAE29C", ""),
                ["covercalibrator/0"] = new("Flat panel", "flat-0"),
            };

        [Test]
        public void FakeEnv_without_a_url_keeps_the_OmniSim_path() {
            Assert.That(LiveAlpacaRig.FromEnvironment(FakeEnv()), Is.Null);
            Assert.That(LiveAlpacaRig.FromEnvironment(FakeEnv(("ARA_ALPACA_URL", "  "), ("ARA_ALPACA_ALLOW_MOTION", "1"))), Is.Null,
                "a blank URL is no rig, whatever the motion flag says");
        }

        [Test]
        public void FakeEnv_url_alone_leaves_motion_off_and_says_how_to_allow_it() {
            var rig = LiveAlpacaRig.FromEnvironment(FakeEnv(("ARA_ALPACA_URL", "http://openastro.lan:6800")))!;
            Assert.That(rig.Host, Is.EqualTo("openastro.lan"));
            Assert.That(rig.Port, Is.EqualTo(6800));
            Assert.That(rig.AllowMotion, Is.False);
            Assert.That(rig.MotionSkipReason("Telescope", "slews the mount"),
                Does.Contain("slews the mount on the Telescope").And.Contain("ARA_ALPACA_ALLOW_MOTION=1"));
        }

        [TestCase("1")]
        [TestCase("true")]
        public void FakeEnv_motion_opt_in_lets_motion_cases_run(string flag) {
            var rig = LiveAlpacaRig.FromEnvironment(FakeEnv(("ARA_ALPACA_URL", "http://openastro.lan:6800"), ("ARA_ALPACA_ALLOW_MOTION", flag)))!;
            Assert.That(rig.AllowMotion, Is.True);
            Assert.That(rig.MotionSkipReason("Focuser", "moves the focuser"), Is.Null);
        }

        [TestCase("ARA_ALPACA_URL", "openastro.lan:6800")]
        [TestCase("ARA_ALPACA_URL", "https://openastro.lan:6800")]
        [TestCase("ARA_ALPACA_CAMERA", "first")]
        [TestCase("ARA_ALPACA_ALLOW_MOTION", "yes")]
        public void FakeEnv_malformed_value_throws_naming_the_variable(string name, string value) {
            var vars = new List<(string, string)> { ("ARA_ALPACA_URL", "http://openastro.lan:6800") };
            vars.RemoveAll(v => v.Item1 == name);
            vars.Add((name, value));
            var ex = Assert.Throws<ArgumentException>(() => LiveAlpacaRig.FromEnvironment(FakeEnv(vars.ToArray())));
            Assert.That(ex!.Message, Does.StartWith(name + "="));
        }

        [Test]
        public void FakeEnv_pick_takes_the_pinned_number_else_the_lowest() {
            var rig = LiveAlpacaRig.FromEnvironment(FakeEnv(("ARA_ALPACA_URL", "http://openastro.lan:6800")))!;
            var lowest = rig.Pick(DeviceType.Camera, Configured)!;
            Assert.That(lowest.AlpacaDeviceNumber, Is.EqualTo(0));
            Assert.That(lowest.UniqueId, Is.EqualTo("cam-0"));
            Assert.That(lowest.HostName, Is.EqualTo("openastro.lan"));
            Assert.That(lowest.IpPort, Is.EqualTo(6800));

            var pinned = LiveAlpacaRig.FromEnvironment(FakeEnv(("ARA_ALPACA_URL", "http://openastro.lan:6800"), ("ARA_ALPACA_CAMERA", "1")))!
                .Pick(DeviceType.Camera, Configured)!;
            Assert.That(pinned.AlpacaDeviceNumber, Is.EqualTo(1));
            Assert.That(pinned.Name, Is.EqualTo("ZWO ASI2600MC Pro"));

            var flat = rig.Pick(DeviceType.FlatDevice, Configured)!;
            Assert.That(flat.Type, Is.EqualTo(DeviceType.FlatDevice), "the flat panel is the server's covercalibrator");
            Assert.That(flat.UniqueId, Is.EqualTo("flat-0"));

            Assert.That(rig.Pick(DeviceType.Telescope, Configured)!.UniqueId, Is.EqualTo("openastro.lan:6800/telescope/0"),
                "a device with no UniqueID gets one from its endpoint");
        }

        [Test]
        public void FakeEnv_pick_is_null_for_a_device_the_server_lacks() {
            var rig = LiveAlpacaRig.FromEnvironment(FakeEnv(("ARA_ALPACA_URL", "http://openastro.lan:6800")))!;
            Assert.That(rig.Pick(DeviceType.Rotator, Configured), Is.Null);
            Assert.That(rig.MissingDeviceMessage(DeviceType.Rotator), Does.Contain("no rotator"));

            var pinned = LiveAlpacaRig.FromEnvironment(FakeEnv(("ARA_ALPACA_URL", "http://openastro.lan:6800"), ("ARA_ALPACA_CAMERA", "3")))!;
            Assert.That(pinned.Pick(DeviceType.Camera, Configured), Is.Null, "a pinned number the server lacks is not swapped for another camera");
            Assert.That(pinned.MissingDeviceMessage(DeviceType.Camera), Does.Contain("camera/3").And.Contain("ARA_ALPACA_CAMERA=3"));
        }
    }
}
