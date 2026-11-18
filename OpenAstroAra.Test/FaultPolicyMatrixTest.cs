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
using System.Linq;

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

        private static SafetyPoliciesDto GuiderPolicies(string? onGuiderLost) =>
            Policies() with { OnGuiderLost = onGuiderLost! };

        [Test]
        public void Pause_and_retry_pauses_a_lost_star_adoptably_with_no_recovery_rung() {
            var plan = FaultPolicyMatrix.Resolve(DeviceType.Guider, EquipmentFaultKind.TrackingLost, GuiderPolicies("pause_and_retry"));
            Assert.Multiple(() => {
                Assert.That(plan.PauseWhileRecovering, Is.True);
                Assert.That(plan.Adoptable, Is.True, "a parked, stopped guider sends no guide step: safety must be able to take the pause over");
                Assert.That(plan.Skip, Is.False);
                Assert.That(plan.RetrySchedule, Is.Empty, "S1: the guider service runs its own recoveries");
                Assert.That(plan.TerminalAction, Is.EqualTo(FaultTerminalAction.None), "give-up leaves the runs paused");
            });
        }

        [Test]
        public void Pause_and_retry_pauses_a_link_down_or_camera_drop_non_adoptably() {
            Assert.Multiple(() => {
                var plan = FaultPolicyMatrix.Resolve(DeviceType.Guider, EquipmentFaultKind.Disconnected, GuiderPolicies("pause_and_retry"));
                Assert.That(plan.PauseWhileRecovering, Is.True);
                Assert.That(plan.Adoptable, Is.False);
                Assert.That(plan.Skip, Is.False);
                Assert.That(plan.RetrySchedule, Is.Empty);
                Assert.That(plan.TerminalAction, Is.EqualTo(FaultTerminalAction.None));
            });
        }

        [Test]
        public void An_unset_or_unknown_on_guider_lost_token_falls_back_to_pause_and_retry() {
            foreach (var token in new string?[] { null, "", "do_a_barrel_roll" }) {
                var plan = FaultPolicyMatrix.Resolve(DeviceType.Guider, EquipmentFaultKind.TrackingLost, GuiderPolicies(token));
                Assert.That(plan.PauseWhileRecovering, Is.True, $"token '{token}'");
            }
            Assert.That(FaultPolicyMatrix.Resolve(DeviceType.Guider, EquipmentFaultKind.TrackingLost, policies: null).PauseWhileRecovering, Is.True);
        }

        [Test]
        public void Skip_target_skips_for_every_guider_kind_without_pausing() {
            foreach (var kind in new[] { EquipmentFaultKind.Disconnected, EquipmentFaultKind.TrackingLost }) {
                var plan = FaultPolicyMatrix.Resolve(DeviceType.Guider, kind, GuiderPolicies("skip_target"));
                Assert.Multiple(() => {
                    Assert.That(plan.Skip, Is.True, kind.ToString());
                    Assert.That(plan.PauseWhileRecovering, Is.False);
                    Assert.That(plan.Adoptable, Is.False);
                    Assert.That(plan.RetrySchedule, Is.Empty);
                    Assert.That(plan.TerminalAction, Is.EqualTo(FaultTerminalAction.None));
                });
            }
        }

        [Test]
        public void Abort_sequence_aborts_without_park_for_every_guider_kind() {
            foreach (var kind in new[] { EquipmentFaultKind.Disconnected, EquipmentFaultKind.TrackingLost }) {
                var plan = FaultPolicyMatrix.Resolve(DeviceType.Guider, kind, GuiderPolicies("abort_sequence"));
                Assert.Multiple(() => {
                    Assert.That(plan.TerminalAction, Is.EqualTo(FaultTerminalAction.Abort), kind.ToString());
                    Assert.That(plan.TerminalAction, Is.Not.EqualTo(FaultTerminalAction.AbortAndPark), "the mount is not parked for a guider fault");
                    Assert.That(plan.Skip, Is.False);
                    Assert.That(plan.PauseWhileRecovering, Is.False);
                    Assert.That(plan.RetrySchedule, Is.Empty);
                });
            }
        }

        [Test]
        public void Only_the_guider_star_lost_row_is_adoptable_and_only_skip_target_skips() {
            foreach (var type in Enum.GetValues<DeviceType>().Where(t => t != DeviceType.Guider)) {
                foreach (var kind in Enum.GetValues<EquipmentFaultKind>()) {
                    var plan = FaultPolicyMatrix.Resolve(type, kind, Policies());
                    Assert.That(plan.Adoptable || plan.Skip, Is.False, $"{type}/{kind}: today's shared rows do not change");
                }
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
                Assert.That(plan.Escalated, Is.False, $"{kind}: a single op fault is instruction-level news");
            }
        }

        [Test]
        public void Persistent_mount_op_faults_escalate_to_abort_and_park() {
            foreach (var kind in new[] { EquipmentFaultKind.StallTimeout, EquipmentFaultKind.OpError }) {
                var plan = FaultPolicyMatrix.Resolve(DeviceType.Telescope, kind, Policies(), persistentOpFault: true)!;
                Assert.That(plan.Escalated, Is.True, $"{kind}");
                Assert.That(plan.TerminalAction, Is.EqualTo(FaultTerminalAction.AbortAndPark),
                    $"{kind}: a mount that keeps failing slews may be against an obstruction — physical safety row");
                Assert.That(plan.RetrySchedule, Is.Empty, "the mount is still connected — nothing to reconnect");
                Assert.That(plan.GiveUpSeverity, Is.EqualTo(NotificationSeverity.Error));
            }
        }

        [Test]
        public void Persistent_camera_op_faults_escalate_to_pause() {
            var plan = FaultPolicyMatrix.Resolve(DeviceType.Camera, EquipmentFaultKind.OpError, Policies(), persistentOpFault: true)!;
            Assert.That(plan.Escalated, Is.True);
            Assert.That(plan.TerminalAction, Is.EqualTo(FaultTerminalAction.PauseSequence),
                "a camera that keeps failing captures is burning sky time for nothing");
            Assert.That(plan.RetrySchedule, Is.Empty);
        }

        [Test]
        public void Persistent_peripheral_op_faults_stay_notify_only() {
            foreach (var deviceType in new[] { DeviceType.Focuser, DeviceType.FilterWheel, DeviceType.Rotator,
                    DeviceType.Switch, DeviceType.Dome, DeviceType.CoverCalibrator }) {
                var plan = FaultPolicyMatrix.Resolve(deviceType, EquipmentFaultKind.StallTimeout, Policies(), persistentOpFault: true)!;
                Assert.That(plan.Escalated, Is.False,
                    $"{deviceType}: its matrix rows all end in 'Notify' — instruction_failed already surfaces each failure");
                Assert.That(plan.TerminalAction, Is.EqualTo(FaultTerminalAction.None), $"{deviceType}");
            }
        }

        [Test]
        public void The_persistence_flag_never_changes_recovery_tracked_kinds() {
            var plan = FaultPolicyMatrix.Resolve(DeviceType.Telescope, EquipmentFaultKind.Disconnected, Policies(),
                persistentOpFault: true)!;
            Assert.That(plan.Escalated, Is.False, "disconnects run the §42.3 reconnect episode, never the op escalation");
            Assert.That(plan.RetrySchedule, Is.EqualTo(FaultPolicyMatrix.HotReconnectLadder));
        }

        [Test]
        public void A_degraded_device_is_notify_only_its_own_reads_are_the_recovery() {
            // 2026-10-16: the wheel and the camera kept answering their probe while every read failed. A
            // ladder "reconnect" on an already-Connected device is a no-op that would report recovered
            // while the reads still fail (review on #1384), so the plan has no attempts.
            foreach (var type in new[] { DeviceType.FilterWheel, DeviceType.Camera, DeviceType.Focuser, DeviceType.Rotator }) {
                var plan = FaultPolicyMatrix.Resolve(type, EquipmentFaultKind.Degraded, policies: null)!;
                Assert.That(plan.RetrySchedule, Is.Empty, type.ToString());
                Assert.That(plan.TerminalAction, Is.EqualTo(FaultTerminalAction.None), "nothing to escalate to: the device still answers");
                Assert.That(plan.PauseWhileRecovering, Is.False, "the instruction that needs the device reports its own failure");
                Assert.That(plan.GiveUpSeverity, Is.EqualTo(NotificationSeverity.Warning));
                Assert.That(plan.Escalated, Is.False);
            }
            var guider = FaultPolicyMatrix.Resolve(DeviceType.Guider, EquipmentFaultKind.Degraded, null);
            Assert.That(guider.PauseWhileRecovering, Is.False, "the guider never publishes Degraded; the shared default is notify-only");
        }
    }
}