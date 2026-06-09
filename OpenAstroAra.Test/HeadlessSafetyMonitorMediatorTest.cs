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
using OpenAstroAra.Sequencer.SequenceItem.SafetyMonitor;
using OpenAstroAra.Server.Services;
using OpenAstroAra.Server.Services.Equipment;
using System.Linq;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §38k-9 — verifies <see cref="HeadlessSafetyMonitorMediator"/> reports
    /// the "not connected" sentinel + no-op operations, and that
    /// <see cref="HeadlessSequencerFactory.WithDefaults"/> picks it up via
    /// the optional ctor parameter so <c>WaitUntilSafe</c> lands as a
    /// resolvable prototype.
    /// </summary>
    [TestFixture]
    public class HeadlessSafetyMonitorMediatorTest {

        [Test]
        public void GetInfo_returns_not_connected_unsafe() {
            var m = new HeadlessSafetyMonitorMediator();
            var info = m.GetInfo();
            Assert.That(info, Is.Not.Null);
            Assert.That(info.Connected, Is.False);
            Assert.That(info.IsSafe, Is.False);
        }

        [Test]
        public async Task Connect_returns_false_without_real_driver() {
            var m = new HeadlessSafetyMonitorMediator();
            var ok = await m.Connect();
            Assert.That(ok, Is.False);
        }

        [Test]
        public async Task Rescan_returns_empty_list() {
            var m = new HeadlessSafetyMonitorMediator();
            var devices = await m.Rescan();
            Assert.That(devices, Is.Empty);
        }

        [Test]
        public void Action_and_SendCommand_return_no_op_defaults() {
            var m = new HeadlessSafetyMonitorMediator();
            Assert.That(m.Action("ping", "arg"), Is.Empty);
            Assert.That(m.SendCommandString("CMD"), Is.Empty);
            Assert.That(m.SendCommandBool("CMD"), Is.False);
            // SendCommandBlind returns void — just verify it doesn't throw.
            Assert.DoesNotThrow(() => m.SendCommandBlind("CMD"));
        }

        // §38k-9 factory integration

        [Test]
        public void WithDefaults_registers_WaitUntilSafe_with_default_stub() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            var typeNames = factory.Items.Select(i => i.GetType().Name).ToList();
            Assert.That(typeNames, Does.Contain("WaitUntilSafe"));
        }

        [Test]
        public void WithDefaults_factory_resolves_WaitUntilSafe_via_prototype_lookup() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            var prototype = factory.GetItem<WaitUntilSafe>();
            Assert.That(prototype, Is.Not.Null);
            Assert.That(prototype, Is.InstanceOf<WaitUntilSafe>());
        }

        [Test]
        public void WithDefaults_accepts_custom_safety_monitor_mediator() {
            // Optional ctor param lets Program.cs hand in a real mediator
            // once Alpaca-backed wiring lands. With the explicit override,
            // WaitUntilSafe gets that mediator, not the default stub.
            var customMediator = new HeadlessSafetyMonitorMediator();
            var factory = HeadlessSequencerFactory.WithDefaults(customMediator);
            Assert.That(factory.GetItem<WaitUntilSafe>(), Is.Not.Null);
        }

        [Test]
        public void WithDefaults_accepts_the_real_SafetyMonitorService_as_mediator() {
            // §14e — the real SafetyMonitorService now backs the mediator surface (the same
            // singleton Program.cs registers for ISafetyMonitorService). The factory must accept
            // it so WaitUntilSafe reads the live device; with nothing connected, GetInfo reports
            // disconnected (verified in SafetyMonitorServiceTest).
            using var svc = new SafetyMonitorService();
            var factory = HeadlessSequencerFactory.WithDefaults(svc);
            Assert.That(factory.GetItem<WaitUntilSafe>(), Is.Not.Null);
        }
    }
}