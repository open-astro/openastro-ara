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
using OpenAstroAra.Server.Services;
using OpenAstroAra.Server.Services.LiveStack;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// RED tests for the ↑ara#1169 live-stack control surface on <see cref="CameraService"/> (planned
    /// <c>ILiveStackControl</c>, implemented by CameraService in a new partial file). Sim-free: guards and idle state only;
    /// the loop hook is covered in CameraServiceLiveStackLifecycleTest
    /// through a synthetic frame source.
    /// </summary>
    [SuppressMessage("Performance", "CA1859:Use concrete types when possible",
        Justification = "The tests exercise the ILiveStackControl surface the endpoints cast to.")]
    [TestFixture]
    public class CameraServiceLiveStackTest {

        // Falsified by: a Start that does not check Live View is active would return OK with nothing feeding
        // the stack (Enabled=true, Frames never grows); the contract is 409 (InvalidOperationException).
        [Test]
        public void StartLiveStack_without_an_active_live_view_throws_InvalidOperation() {
            using var svc = new CameraService();
            Assert.Throws<InvalidOperationException>(() => ((ILiveStackControl)svc).StartLiveStack());
        }

        // Falsified by: a status that is null/throws before first use, or reports Enabled=true by default.
        [Test]
        public void Idle_status_is_disabled_with_zero_counts_and_no_preview() {
            using var svc = new CameraService();
            var c = (ILiveStackControl)svc;
            var s = c.GetLiveStackStatus();
            Assert.That(s.Enabled, Is.False);
            Assert.That(s.FramesStacked, Is.EqualTo(0));
            Assert.That(s.FramesUnregistered, Is.EqualTo(0));
            Assert.That(s.StackSeq, Is.EqualTo(0));
            Assert.That(c.GetLiveStackFrame(), Is.Null);
        }

        // Falsified by: Reset/Stop that throw when nothing is stacked would turn a harmless double-click into a 500.
        [Test]
        public void Reset_and_Stop_are_idempotent_when_nothing_is_stacked() {
            using var svc = new CameraService();
            var c = (ILiveStackControl)svc;
            Assert.DoesNotThrow(() => c.ResetLiveStack());
            Assert.DoesNotThrow(() => c.StopLiveStack());
            Assert.That(c.GetLiveStackStatus().Enabled, Is.False);
        }
    }
}
