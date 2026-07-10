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

    /// <summary>§42.2 items 2+5 — the op-fault persistence counter: sliding window, definite
    /// op failures only, fire-once-then-fresh-streak, per-device isolation.</summary>
    [TestFixture]
    public class OpFaultEscalatorTest {

        private static readonly DateTimeOffset T0 = new(2026, 7, 10, 5, 0, 0, TimeSpan.Zero);

        [Test]
        public void Fires_on_the_third_definite_op_fault_within_the_window() {
            var e = new OpFaultEscalator();
            Assert.That(e.Observe(DeviceType.Telescope, EquipmentFaultKind.OpError, T0), Is.False);
            Assert.That(e.Observe(DeviceType.Telescope, EquipmentFaultKind.OpError, T0 + TimeSpan.FromMinutes(1)), Is.False);
            Assert.That(e.Observe(DeviceType.Telescope, EquipmentFaultKind.OpError, T0 + TimeSpan.FromMinutes(2)), Is.True,
                "three failures in two minutes is persistence, not bad luck");
        }

        [Test]
        public void Stall_timeouts_and_op_errors_count_toward_the_same_streak() {
            var e = new OpFaultEscalator();
            e.Observe(DeviceType.Telescope, EquipmentFaultKind.StallTimeout, T0);
            e.Observe(DeviceType.Telescope, EquipmentFaultKind.OpError, T0 + TimeSpan.FromMinutes(1));
            Assert.That(e.Observe(DeviceType.Telescope, EquipmentFaultKind.StallTimeout, T0 + TimeSpan.FromMinutes(2)),
                Is.True, "a slew that stalls and a slew the driver rejects are the same persistence story");
        }

        [Test]
        public void Advisory_and_recovery_tracked_kinds_never_count() {
            var e = new OpFaultEscalator();
            foreach (var kind in new[] {
                EquipmentFaultKind.ValueMismatch, EquipmentFaultKind.CoolingDrift,
                EquipmentFaultKind.Disconnected, EquipmentFaultKind.TrackingLost,
            }) {
                for (var i = 0; i < 5; i++) {
                    Assert.That(e.Observe(DeviceType.Telescope, kind, T0 + TimeSpan.FromMinutes(i)), Is.False,
                        $"{kind} has its own matrix row — it must never feed the op-failure streak");
                }
            }
            Assert.That(e.Observe(DeviceType.Telescope, EquipmentFaultKind.OpError, T0), Is.False,
                "the advisory storm above left the op-failure count at zero");
        }

        [Test]
        public void Faults_older_than_the_window_age_out() {
            var e = new OpFaultEscalator();
            e.Observe(DeviceType.Telescope, EquipmentFaultKind.OpError, T0);
            e.Observe(DeviceType.Telescope, EquipmentFaultKind.OpError, T0 + TimeSpan.FromMinutes(1));
            Assert.That(e.Observe(DeviceType.Telescope, EquipmentFaultKind.OpError, T0 + OpFaultEscalator.DefaultWindow + TimeSpan.FromMinutes(2)),
                Is.False, "a failed slew at dusk must not arm a hair-trigger for midnight");
        }

        [Test]
        public void A_fired_escalation_requires_a_full_fresh_streak_to_fire_again() {
            var e = new OpFaultEscalator();
            e.Observe(DeviceType.Telescope, EquipmentFaultKind.OpError, T0);
            e.Observe(DeviceType.Telescope, EquipmentFaultKind.OpError, T0);
            Assert.That(e.Observe(DeviceType.Telescope, EquipmentFaultKind.OpError, T0), Is.True);
            Assert.That(e.Observe(DeviceType.Telescope, EquipmentFaultKind.OpError, T0 + TimeSpan.FromSeconds(10)), Is.False,
                "the fourth fault right after an escalation must not immediately escalate again");
            Assert.That(e.Observe(DeviceType.Telescope, EquipmentFaultKind.OpError, T0 + TimeSpan.FromSeconds(20)), Is.False);
            Assert.That(e.Observe(DeviceType.Telescope, EquipmentFaultKind.OpError, T0 + TimeSpan.FromSeconds(30)), Is.True,
                "a full fresh streak earns the next escalation");
        }

        [Test]
        public void Devices_count_independently() {
            var e = new OpFaultEscalator();
            e.Observe(DeviceType.Telescope, EquipmentFaultKind.OpError, T0);
            e.Observe(DeviceType.Telescope, EquipmentFaultKind.OpError, T0);
            e.Observe(DeviceType.Camera, EquipmentFaultKind.OpError, T0);
            e.Observe(DeviceType.Camera, EquipmentFaultKind.OpError, T0);
            Assert.That(e.Observe(DeviceType.Camera, EquipmentFaultKind.OpError, T0), Is.True,
                "the camera's third fault fires the camera");
            Assert.That(e.Observe(DeviceType.Telescope, EquipmentFaultKind.OpError, T0), Is.True,
                "the mount's own third fault fires the mount — the camera's escalation didn't consume it");
        }
    }
}
