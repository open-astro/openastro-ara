#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Moq;
using NUnit.Framework;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System.Collections.Generic;

namespace OpenAstroAra.Test {

    /// <summary>#1308 — the guide camera is pinned by Alpaca UniqueID, not by the device number
    /// AlpacaBridge can renumber across a restart; and it must never be the main imaging camera.</summary>
    [TestFixture]
    public class GuiderCameraPinCheckTest {

        private const string Mini = "ZWO_0";                       // the serial-less ASI120MM Mini
        private const string Pro = "ZWO_SN_0c190e111d020900";      // the ASI533MC Pro

        private static IGuiderSettings Settings(string camera, string pinned = "") {
            var s = new Mock<IGuiderSettings>();
            s.SetupGet(x => x.GuiderCamera).Returns(camera);
            s.SetupGet(x => x.GuiderCameraAlpacaUniqueId).Returns(pinned);
            return s.Object;
        }

        private static Dictionary<string, AlpacaConfiguredDevice> Before() => new() {
            ["camera/0"] = new("ZWO ASI533MC Pro", Pro),
            ["camera/1"] = new("ZWO ASI120MM Mini", Mini),
            ["telescope/1"] = new("Sky-Watcher EQM-35 Pro (EQMOD)", "EQMOD_1"),
        };

        private static Dictionary<string, AlpacaConfiguredDevice> AfterReboot() => new() {
            ["camera/0"] = new("ZWO ASI120MM Mini", Mini),
            ["camera/1"] = new("ZWO ASI533MC Pro", Pro),
        };

        private static DiscoveredDeviceDto MainCamera(int number, string uniqueId = Pro) =>
            new(uniqueId, "ZWO ASI533MC Pro", DeviceType.Camera, "openastro", "127.0.0.1", 6800, number, false);

        [Test]
        public void A_camera_still_at_its_number_with_its_id_passes() {
            Assert.That(GuiderCameraPinCheck.Describe(Settings("Alpaca Camera [127.0.0.1:6800/1]", Mini), Before(), MainCamera(0)), Is.Null);
        }

        [Test]
        public void A_renumbered_camera_is_refused_naming_where_it_went() {
            // The field case: after the reboot camera/1 is the imaging camera and the Mini is camera/0.
            var reason = GuiderCameraPinCheck.Describe(Settings("Alpaca Camera [127.0.0.1:6800/1]", Mini), AfterReboot(), MainCamera(1));
            Assert.That(reason, Does.Contain("has moved"));
            Assert.That(reason, Does.Contain("now camera/0"));
            Assert.That(reason, Does.Contain("camera/1 is 'ZWO ASI533MC Pro'"));
        }

        [Test]
        public void A_camera_no_longer_offered_is_refused() {
            var only = new Dictionary<string, AlpacaConfiguredDevice> { ["camera/0"] = new("ZWO ASI533MC Pro", Pro) };
            var reason = GuiderCameraPinCheck.Describe(Settings("Alpaca Camera [127.0.0.1:6800/1]", Mini), only, null);
            Assert.That(reason, Does.Contain("no longer offered"));
        }

        [Test]
        public void A_swapped_camera_in_the_same_slot_passes_unless_it_is_the_main_camera() {
            // #1362 — the picked camera is gone and another answers at its number: the connect-time reconcile
            // re-pins it. The same slot holding the imaging camera is still refused.
            var swapped = new Dictionary<string, AlpacaConfiguredDevice> {
                ["camera/0"] = new("ZWO ASI533MC Pro", Pro),
                ["camera/1"] = new("QHY5III585M", "QHY_585_0042"),
            };
            Assert.That(GuiderCameraPinCheck.Describe(Settings("Alpaca Camera [127.0.0.1:6800/1]", Mini), swapped, MainCamera(0)), Is.Null);
            Assert.That(GuiderCameraPinCheck.IsMainCamera(Settings("Alpaca Camera [127.0.0.1:6800/1]", Mini), swapped, MainCamera(0)), Is.False);
            var toMain = new Dictionary<string, AlpacaConfiguredDevice> { ["camera/1"] = new("ZWO ASI533MC Pro", Pro) };
            Assert.That(GuiderCameraPinCheck.Describe(Settings("Alpaca Camera [127.0.0.1:6800/1]", Mini), toMain, MainCamera(1)), Does.Contain("is the main imaging camera"));
            Assert.That(GuiderCameraPinCheck.IsMainCamera(Settings("Alpaca Camera [127.0.0.1:6800/1]", Mini), toMain, MainCamera(1)), Is.True);
        }

        [Test]
        public void The_main_imaging_camera_is_refused_by_identity_and_by_address() {
            // By UniqueID: the pin is fine but the device at that number IS the imaging camera.
            var byId = GuiderCameraPinCheck.Describe(Settings("Alpaca Camera [127.0.0.1:6800/0]", Pro), Before(), MainCamera(0));
            Assert.That(byId, Does.Contain("is the main imaging camera"));
            // By address with no device list (management API unreachable): same server, same number.
            var byNumber = GuiderCameraPinCheck.Describe(Settings("Alpaca Camera [localhost:6800/0]"), new Dictionary<string, AlpacaConfiguredDevice>(), MainCamera(0));
            Assert.That(byNumber, Does.Contain("is the main imaging camera"), "loopback spellings are the same host");
            // A different number on the same server is fine.
            Assert.That(GuiderCameraPinCheck.Describe(Settings("Alpaca Camera [127.0.0.1:6800/1]"), new Dictionary<string, AlpacaConfiguredDevice>(), MainCamera(0)), Is.Null);
        }

        [Test]
        public void An_unpinned_pre_1308_selection_skips_the_identity_check_but_not_the_main_camera_one() {
            Assert.That(GuiderCameraPinCheck.Describe(Settings("Alpaca Camera [127.0.0.1:6800/1]"), Before(), MainCamera(0)), Is.Null,
                "nothing stored to compare against, and camera/1 is the Mini");
            Assert.That(GuiderCameraPinCheck.Describe(Settings("Alpaca Camera [127.0.0.1:6800/1]"), AfterReboot(), MainCamera(1)),
                Does.Contain("is the main imaging camera"));
        }

        [Test]
        public void A_stale_main_camera_number_does_not_refuse_a_camera_the_ids_say_is_different() {
            // AlpacaBridge renumbered while the daemon kept running: the retained main camera still says
            // number 0, but the server shows camera/0 is now the Mini. Identity wins.
            Assert.That(GuiderCameraPinCheck.Describe(Settings("Alpaca Camera [127.0.0.1:6800/0]", Mini), AfterReboot(), MainCamera(0)), Is.Null);
        }

        [Test]
        public void The_same_serial_less_id_on_another_server_is_a_different_camera() {
            var other = new Dictionary<string, AlpacaConfiguredDevice> { ["camera/0"] = new("ZWO ASI120MM Mini", Pro) };
            Assert.That(GuiderCameraPinCheck.Describe(Settings("Alpaca Camera [10.0.0.5:6800/0]", Pro), other, MainCamera(0)), Is.Null,
                "ids are per server; 10.0.0.5 is not where the imaging camera lives");
        }

        [Test]
        public void An_unreachable_server_skips_the_identity_check() {
            // Empty devices = lookup failed; the pinned id cannot be checked, so the push is not blocked on it.
            Assert.That(GuiderCameraPinCheck.Describe(Settings("Alpaca Camera [10.0.0.5:6800/1]", Mini), new Dictionary<string, AlpacaConfiguredDevice>(), MainCamera(0)), Is.Null);
        }

        [Test]
        public void Non_alpaca_bare_and_unset_selections_are_not_checked() {
            Assert.That(GuiderCameraPinCheck.Describe(Settings("Simulator", Mini), Before(), MainCamera(0)), Is.Null);
            Assert.That(GuiderCameraPinCheck.Describe(Settings("Alpaca Camera", Mini), Before(), MainCamera(0)), Is.Null);
            Assert.That(GuiderCameraPinCheck.Describe(Settings("", Mini), Before(), MainCamera(0)), Is.Null);
        }
    }
}
