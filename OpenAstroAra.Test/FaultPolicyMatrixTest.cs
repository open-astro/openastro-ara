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

    /// <summary>§42.2 — the pure policy table: every reaction decision is resolvable
    /// without a daemon, so every row is asserted here.</summary>
    [TestFixture]
    public class FaultPolicyMatrixTest {

        private static SafetyPoliciesDto Policies(
                bool hotReconnect = true,
                string onCameraLost = "reconnect_then_pause",
                string onMountLost = "reconnect_then_abort_park",
                string onTrackingLost = "reenable_then_pause") =>
            new(OnUnsafe: "pause_and_park", AutoResumeWhenSafe: true, ResumeDelayMin: 1,
                MeridianFlipAuto: true, MeridianPauseMin: 2, MeridianRecenter: true, MeridianRecalGuider: false,
                OnAltitudeLimit: "pause_sequence", ParkIfNoMoreTargets: true, OnGuiderLost: "pause_and_retry",
                GuiderRetryTimeoutSec: 60, SkipTargetIfRecoveryFails: false,
                HotReconnectEnabled: hotReconnect, OnCameraLost: onCameraLost,
                OnMountLost: onMountLost, OnTrackingLost: onTrackingLost);

        [Test]
        public void The_guider_is_never_the_reaction_services_to_handle() {
            foreach (var kind in Enum.GetValues<EquipmentFaultKind>()) {
                Assert.That(FaultPolicyMatrix.Resolve(DeviceType.Guider, kind, Policies()), Is.Null,
                    $"{kind}: GuiderService.FaultReaction owns guider faults — a duplicate reaction here would double-pause (#760 regression)");
            }
        }

        [Test]
        public void The_ladder_is_the_spec_backoff() {
            Assert.That(FaultPolicyMatrix.HotReconnectLadder, Is.EqualTo(new[] {
                TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15),
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60),
            }), "§42.3: attempt immediately, then 5/15/30/60 s");
        }

        [Test]
        public void A_lost_camera_defaults_to_reconnect_then_pause() {
            var plan = FaultPolicyMatrix.Resolve(DeviceType.Camera, EquipmentFaultKind.Disconnected, Policies())!;
            Assert.That(plan.RetrySchedule, Is.EqualTo(FaultPolicyMatrix.HotReconnectLadder));
            Assert.That(plan.TerminalAction, Is.EqualTo(FaultTerminalAction.PauseSequence));
            Assert.That(plan.GiveUpSeverity, Is.EqualTo(NotificationSeverity.Error));
            Assert.That(plan.PauseWhileRecovering, Is.True, "a sequence must not keep exposing into a dead camera");
        }

        [Test]
        public void A_lost_mount_defaults_to_reconnect_then_abort_park() {
            var plan = FaultPolicyMatrix.Resolve(DeviceType.Telescope, EquipmentFaultKind.Disconnected, Policies())!;
            Assert.That(plan.RetrySchedule, Has.Count.EqualTo(5));
            Assert.That(plan.TerminalAction, Is.EqualTo(FaultTerminalAction.AbortAndPark));
            Assert.That(plan.PauseWhileRecovering, Is.True);
        }

        [Test]
        public void Lost_tracking_defaults_to_reenable_then_pause() {
            var plan = FaultPolicyMatrix.Resolve(DeviceType.Telescope, EquipmentFaultKind.TrackingLost, Policies())!;
            Assert.That(plan.RetrySchedule, Has.Count.EqualTo(5));
            Assert.That(plan.TerminalAction, Is.EqualTo(FaultTerminalAction.PauseSequence));
            Assert.That(plan.PauseWhileRecovering, Is.True);
        }

        [Test]
        public void A_lost_peripheral_reconnects_quietly_without_pausing_the_night() {
            foreach (var type in new[] { DeviceType.Focuser, DeviceType.FilterWheel, DeviceType.Rotator,
                    DeviceType.Dome, DeviceType.Switch, DeviceType.FlatDevice, DeviceType.CoverCalibrator,
                    DeviceType.SafetyMonitor, DeviceType.ObservingConditions }) {
                var plan = FaultPolicyMatrix.Resolve(type, EquipmentFaultKind.Disconnected, Policies())!;
                Assert.That(plan.RetrySchedule, Has.Count.EqualTo(5), $"{type} reconnects");
                Assert.That(plan.TerminalAction, Is.EqualTo(FaultTerminalAction.None), $"{type} never halts the sequence itself");
                Assert.That(plan.GiveUpSeverity, Is.EqualTo(NotificationSeverity.Warning), $"{type}");
                Assert.That(plan.PauseWhileRecovering, Is.False, $"{type}");
            }
        }

        [Test]
        public void Disabling_hot_reconnect_goes_straight_to_the_terminal_action() {
            var plan = FaultPolicyMatrix.Resolve(DeviceType.Camera, EquipmentFaultKind.Disconnected,
                Policies(hotReconnect: false))!;
            Assert.That(plan.RetrySchedule, Is.Empty);
            Assert.That(plan.TerminalAction, Is.EqualTo(FaultTerminalAction.PauseSequence));
        }

        [Test]
        public void The_pause_policy_skips_retries_and_the_notify_only_policy_skips_everything() {
            var pause = FaultPolicyMatrix.Resolve(DeviceType.Camera, EquipmentFaultKind.Disconnected,
                Policies(onCameraLost: "pause"))!;
            Assert.That(pause.RetrySchedule, Is.Empty);
            Assert.That(pause.TerminalAction, Is.EqualTo(FaultTerminalAction.PauseSequence));

            var notify = FaultPolicyMatrix.Resolve(DeviceType.Camera, EquipmentFaultKind.Disconnected,
                Policies(onCameraLost: "notify_only"))!;
            Assert.That(notify.RetrySchedule, Is.Empty);
            Assert.That(notify.TerminalAction, Is.EqualTo(FaultTerminalAction.None));
            Assert.That(notify.PauseWhileRecovering, Is.False, "nothing to recover toward — no pause");
        }

        [Test]
        public void An_unknown_policy_token_falls_back_to_the_devices_default() {
            var plan = FaultPolicyMatrix.Resolve(DeviceType.Telescope, EquipmentFaultKind.Disconnected,
                Policies(onMountLost: "self_destruct"))!;
            Assert.That(plan.TerminalAction, Is.EqualTo(FaultTerminalAction.AbortAndPark),
                "a token from a newer client must not silently disable the reaction");
            Assert.That(plan.RetrySchedule, Has.Count.EqualTo(5));
        }

        [Test]
        public void Null_policies_resolve_to_the_defaults() {
            var plan = FaultPolicyMatrix.Resolve(DeviceType.Camera, EquipmentFaultKind.Disconnected, null)!;
            Assert.That(plan.RetrySchedule, Has.Count.EqualTo(5));
            Assert.That(plan.TerminalAction, Is.EqualTo(FaultTerminalAction.PauseSequence));
        }

        [Test]
        public void Op_faults_are_surfaced_but_not_reconnected() {
            foreach (var kind in new[] { EquipmentFaultKind.StallTimeout, EquipmentFaultKind.OpError,
                    EquipmentFaultKind.ValueMismatch, EquipmentFaultKind.CoolingDrift }) {
                var plan = FaultPolicyMatrix.Resolve(DeviceType.Camera, kind, Policies())!;
                Assert.That(plan.RetrySchedule, Is.Empty, $"{kind}: the device is still connected — nothing to reconnect");
                Assert.That(plan.TerminalAction, Is.EqualTo(FaultTerminalAction.None), $"{kind}");
            }
        }
    }
}
