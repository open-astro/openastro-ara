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
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Profile.Interfaces;
using System.Collections.Generic;
using System.Linq;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #2074 — <see cref="PHD2Guider.PlanEngineConfigPush"/>: the §63.5 push leaves out the equipment
    /// selections and the profile setup the guider already holds, so a reconnect does not rebuild the
    /// guider's mount and drop its calibration.
    /// </summary>
    [TestFixture]
    public class PHD2GuiderPushPlanTest {

        private const string Camera = "Alpaca Camera [bridge.local:11111/0]";
        private const string Mount = "Alpaca Telescope [bridge.local:11111/0]";

        private static IReadOnlyList<Phd2Method> Messages(string camera = Camera, string mount = Mount,
                string? auxMount = null, bool? multiStar = true, string? cameraId = null) {
            var s = new Mock<IGuiderSettings>();
            s.SetupGet(x => x.GuiderCamera).Returns(camera);
            s.SetupGet(x => x.GuiderCameraId).Returns(cameraId!);
            s.SetupGet(x => x.GuiderMount).Returns(mount);
            s.SetupGet(x => x.GuiderAuxMount).Returns(auxMount!);
            s.SetupGet(x => x.GuideFocalLength).Returns(240);
            s.SetupGet(x => x.GuidePixelSize).Returns(2.9);
            s.SetupGet(x => x.DecGuideMode).Returns("auto");
            return PHD2Guider.BuildGuiderEngineConfigMessages(s.Object, new GuiderAdvancedSettings { MultiStarEnabled = multiStar });
        }

        /// <summary>What a guider that already holds <see cref="Messages"/> reports.</summary>
        private static GuiderEquipmentState Held(bool? calibrated = true) => new(
            Camera: Camera,
            CameraId: "ASI120MM-S",
            Mount: Mount,
            Rotator: PHD2Guider.NoRotatorChoice,
            Alpaca: new GuiderAlpacaServerState("bridge.local", 11111, 0, 0, 0),
            FocalLength: 240,
            PixelSize: 2.9,
            MultiStar: true,
            Calibrated: calibrated);

        private static readonly string[] SelectionGroup = ["set_alpaca_server", "set_selected_camera", "set_selected_mount", "set_selected_rotator"];
        private static readonly string[] SetupOnly = ["set_profile_setup"];

        private static string[] Methods(GuiderEnginePushPlan plan) => plan.Messages.Select(m => m.Method).ToArray();

        [Test]
        public void A_guider_that_holds_every_selection_and_setup_gets_no_disconnect_window() {
            var plan = PHD2Guider.PlanEngineConfigPush(Messages(), Held());

            Assert.That(plan.OpensDisconnectWindow, Is.False);
            Assert.That(plan.Messages.Where(PHD2Guider.RequiresDisconnectedEquipment), Is.Empty);
            Assert.That(Methods(plan), Does.Contain("set_dec_guide_mode"), "the runtime-safe messages still go out");
            Assert.That((plan.SelectionsSkipped, plan.SetupSkipped, plan.RestoreCalibrationAfterReconnect), Is.EqualTo((true, true, false)));
        }

        [Test]
        public void Selections_that_differ_only_in_case_or_spaces_count_as_unchanged() {
            var state = Held() with { Camera = "  alpaca camera [BRIDGE.local:11111/0] ", Alpaca = new GuiderAlpacaServerState("BRIDGE.LOCAL", 11111, 0, 0, 0) };

            Assert.That(PHD2Guider.SelectionsUnchanged(Messages(), state), Is.True);
        }

        [Test]
        public void A_different_device_number_host_or_mount_counts_as_changed() {
            Assert.That(PHD2Guider.SelectionsUnchanged(Messages(), Held() with { Alpaca = new GuiderAlpacaServerState("bridge.local", 11111, 1, 0, 0) }),
                Is.False, "camera device number");
            Assert.That(PHD2Guider.SelectionsUnchanged(Messages(), Held() with { Alpaca = new GuiderAlpacaServerState("rc91.lan", 11111, 0, 0, 0) }),
                Is.False, "host");
            Assert.That(PHD2Guider.SelectionsUnchanged(Messages(), Held() with { Alpaca = new GuiderAlpacaServerState("bridge.local", 6800, 0, 0, 0) }),
                Is.False, "port");
            Assert.That(PHD2Guider.SelectionsUnchanged(Messages(), Held() with { Mount = "On-camera" }), Is.False, "mount");
            Assert.That(PHD2Guider.SelectionsUnchanged(Messages(), Held() with { Rotator = "Alpaca Rotator [bridge.local:11111/0]" }),
                Is.False, "a rotator the guider still holds on a guide scope");
        }

        [Test]
        public void A_camera_id_compares_exactly() {
            Assert.That(PHD2Guider.SelectionsUnchanged(Messages(cameraId: "ASI120MM-S"), Held()), Is.True);
            Assert.That(PHD2Guider.SelectionsUnchanged(Messages(cameraId: "asi120mm-s"), Held()), Is.False);
        }

        [Test]
        public void A_field_the_guider_did_not_report_counts_as_changed() {
            Assert.That(PHD2Guider.SelectionsUnchanged(Messages(), Held() with { Camera = null }), Is.False, "camera");
            Assert.That(PHD2Guider.SelectionsUnchanged(Messages(), Held() with { Alpaca = null }), Is.False, "alpaca server");
            Assert.That(PHD2Guider.SelectionsUnchanged(Messages(), Held() with { Alpaca = new GuiderAlpacaServerState("bridge.local", null, 0, 0, 0) }),
                Is.False, "alpaca port");
            Assert.That(PHD2Guider.SetupUnchanged(Messages(), Held() with { PixelSize = null }), Is.False, "pixel size");
            Assert.That(PHD2Guider.SetupUnchanged(Messages(), Held() with { MultiStar = null }), Is.False, "multi-star");
            var plan = PHD2Guider.PlanEngineConfigPush(Messages(), null);
            Assert.That(plan.Messages, Has.Count.EqualTo(Messages().Count), "no state at all sends everything, as before #2074");
            Assert.That(plan.OpensDisconnectWindow, Is.True);
        }

        [Test]
        public void A_bare_alpaca_selection_never_matches_the_guiders_full_choice() {
            // The daemon resolves "Alpaca Camera" to its one Alpaca choice and stores that full string.
            Assert.That(PHD2Guider.SelectionsUnchanged(Messages(camera: "Alpaca Camera"), Held()), Is.False);
        }

        [Test]
        public void An_aux_mount_rides_the_selection_group_and_never_decides_it() {
            var plan = PHD2Guider.PlanEngineConfigPush(Messages(auxMount: "Alpaca Telescope [bridge.local:11111/1]"), Held());

            Assert.That(Methods(plan), Does.Not.Contain("set_selected_aux_mount"), "this guider refuses it always");
            Assert.That(plan.OpensDisconnectWindow, Is.False);

            var changed = PHD2Guider.PlanEngineConfigPush(Messages(auxMount: "Alpaca Telescope [bridge.local:11111/1]"), Held() with { Mount = "On-camera" });
            Assert.That(Methods(changed), Does.Contain("set_selected_aux_mount"), "a changed group goes out whole");
        }

        [Test]
        public void A_list_without_disconnect_messages_is_unchanged() {
            var tuning = Messages().Where(m => !PHD2Guider.RequiresDisconnectedEquipment(m)).ToList();

            Assert.That(PHD2Guider.SelectionsUnchanged(tuning, null), Is.True);
            Assert.That(PHD2Guider.SetupUnchanged(tuning, null), Is.True);
            Assert.That(PHD2Guider.PlanEngineConfigPush(tuning, null).OpensDisconnectWindow, Is.False);
        }

        [Test]
        public void A_changed_camera_sends_the_whole_selection_group_in_order() {
            var plan = PHD2Guider.PlanEngineConfigPush(Messages(camera: "Alpaca Camera [bridge.local:11111/1]"), Held());

            var sent = Methods(plan).Where(m => m.StartsWith("set_selected", System.StringComparison.Ordinal) || m == "set_alpaca_server").ToArray();
            Assert.That(sent, Is.EqualTo(SelectionGroup));
            Assert.That(Methods(plan), Does.Not.Contain("set_profile_setup"), "the setup is unchanged and goes on its own");
            Assert.That(plan.OpensDisconnectWindow, Is.True);
            Assert.That(plan.RestoreCalibrationAfterReconnect, Is.False, "a different camera needs a new calibration");
        }

        [Test]
        public void A_multi_star_only_change_sends_the_setup_alone_and_restores_after_the_reconnect() {
            var plan = PHD2Guider.PlanEngineConfigPush(Messages(multiStar: false), Held());

            Assert.That(plan.Messages.Where(PHD2Guider.RequiresDisconnectedEquipment).Select(m => m.Method), Is.EqualTo(SetupOnly));
            Assert.That(plan.SelectionsSkipped, Is.True);
            Assert.That(plan.RestoreCalibrationAfterReconnect, Is.True);
        }

        [TestCase(false)]
        [TestCase(null)]
        public void An_uncalibrated_or_unknown_guider_gets_no_restore(bool? calibrated) {
            var plan = PHD2Guider.PlanEngineConfigPush(Messages(multiStar: false), Held(calibrated));

            Assert.That(plan.OpensDisconnectWindow, Is.True);
            Assert.That(plan.RestoreCalibrationAfterReconnect, Is.False);
        }

        [Test]
        public void Changed_optics_get_no_restore() {
            Assert.That(PHD2Guider.PlanEngineConfigPush(Messages(), Held() with { FocalLength = 200 }).RestoreCalibrationAfterReconnect,
                Is.False, "focal length");
            Assert.That(PHD2Guider.PlanEngineConfigPush(Messages(), Held() with { PixelSize = 3.75 }).RestoreCalibrationAfterReconnect,
                Is.False, "pixel size");
            Assert.That(PHD2Guider.PlanEngineConfigPush(Messages(multiStar: false), Held() with { Alpaca = new GuiderAlpacaServerState("rc91.lan", 11111, 0, 0, 0) })
                .RestoreCalibrationAfterReconnect, Is.False, "alpaca server");
            Assert.That(PHD2Guider.SetupUnchanged(Messages(), Held() with { PixelSize = 2.9 + 1e-9 }), Is.True, "a rounding difference is not a change");
        }

        [TestCase(3.7599999904632568, 3.76, true)] // an Alpaca driver's float, read back through the guider's %g
        [TestCase(12.3456789, 12.3457, true)]      // more than six significant digits
        [TestCase(3.76, 3.75, false)]
        [TestCase(2.9, 2.91, false)]
        public void The_pixel_size_compares_within_the_guiders_six_digit_rounding(double pushed, double held, bool unchanged) {
            var setup = new Phd2Method[] { new Phd2SetProfileSetup { Parameters = new Phd2ProfileSetupParameter { PixelSize = pushed } } };

            Assert.That(PHD2Guider.SetupUnchanged(setup, Held() with { PixelSize = held }), Is.EqualTo(unchanged));
        }
    }
}
