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
using OpenAstroAra.Core.Model;
using OpenAstroAra.Sequencer.SequenceItem.Camera;
using OpenAstroAra.Server.Services;
using OpenAstroAra.Server.Services.Equipment;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §38k-13 — verifies <see cref="HeadlessCameraMediator"/> sentinel
    /// behavior (including the throw-contract on the exposure-producing
    /// members) + that the five camera-control instructions register and
    /// resolve via the factory.
    /// </summary>
    [TestFixture]
    public class HeadlessCameraMediatorTest {

        private static readonly IProgress<ApplicationStatus> NoProgress = new Progress<ApplicationStatus>();

        [Test]
        public void GetInfo_returns_not_connected() {
            var m = new HeadlessCameraMediator();
            Assert.That(m.GetInfo().Connected, Is.False);
        }

        [Test]
        public async Task CoolCamera_returns_false() {
            var m = new HeadlessCameraMediator();
            Assert.That(await m.CoolCamera(-10.0, TimeSpan.FromMinutes(1), NoProgress, CancellationToken.None), Is.False);
        }

        [Test]
        public async Task WarmCamera_returns_false() {
            var m = new HeadlessCameraMediator();
            Assert.That(await m.WarmCamera(TimeSpan.FromMinutes(1), NoProgress, CancellationToken.None), Is.False);
        }

        [Test]
        public void AtTargetTemp_is_false() {
            var m = new HeadlessCameraMediator();
            Assert.That(m.AtTargetTemp, Is.False);
        }

        [Test]
        public void TargetTemp_is_NaN() {
            var m = new HeadlessCameraMediator();
            Assert.That(m.TargetTemp, Is.NaN);
        }

        [Test]
        public void IsFreeToCapture_returns_true() {
            var m = new HeadlessCameraMediator();
            Assert.That(m.IsFreeToCapture(new object()), Is.True);
        }

        [Test]
        public void Control_ops_do_not_throw() {
            var m = new HeadlessCameraMediator();
            Assert.DoesNotThrow(() => m.SetUSBLimit(40));
            Assert.DoesNotThrow(() => m.SetReadoutMode(0));
            Assert.DoesNotThrow(() => m.SetReadoutModeForNormalImages(1));
            Assert.DoesNotThrow(() => m.SetBinning(2, 2));
            Assert.DoesNotThrow(() => m.SetDewHeater(true));
            Assert.DoesNotThrow(() => m.AbortExposure());
        }

        // Exposure-producing members have no honest headless sentinel and
        // throw rather than fabricate an IExposureData (documented contract).

        [Test]
        public void Download_throws_not_supported() {
            var m = new HeadlessCameraMediator();
            Assert.Throws<NotSupportedException>(() => m.Download(CancellationToken.None));
        }

        [Test]
        public void LiveView_throws_not_supported() {
            var m = new HeadlessCameraMediator();
            Assert.Throws<NotSupportedException>(() => m.LiveView(CancellationToken.None));
        }

        [Test]
        public void GetDevice_throws_not_supported() {
            var m = new HeadlessCameraMediator();
            Assert.Throws<NotSupportedException>(() => m.GetDevice());
        }

        // §38k-13 factory integration — 5 new instructions

        [Test]
        public void WithDefaults_registers_CoolCamera() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            Assert.That(factory.Items.Select(i => i.GetType().Name), Does.Contain("CoolCamera"));
        }

        [Test]
        public void WithDefaults_registers_WarmCamera() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            Assert.That(factory.Items.Select(i => i.GetType().Name), Does.Contain("WarmCamera"));
        }

        [Test]
        public void WithDefaults_registers_SetUSBLimit() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            Assert.That(factory.Items.Select(i => i.GetType().Name), Does.Contain("SetUSBLimit"));
        }

        [Test]
        public void WithDefaults_registers_SetReadoutMode() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            Assert.That(factory.Items.Select(i => i.GetType().Name), Does.Contain("SetReadoutMode"));
        }

        [Test]
        public void WithDefaults_registers_DewHeater() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            Assert.That(factory.Items.Select(i => i.GetType().Name), Does.Contain("DewHeater"));
        }

        [Test]
        public void WithDefaults_factory_resolves_CoolCamera_via_prototype_lookup() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            var prototype = factory.GetItem<CoolCamera>();
            Assert.That(prototype, Is.Not.Null);
            Assert.That(prototype, Is.InstanceOf<CoolCamera>());
        }
    }
}
