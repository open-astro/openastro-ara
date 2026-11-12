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
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Alpaca;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CaptureSequence = OpenAstroAra.Equipment.Model.CaptureSequence;
using DeviceType = OpenAstroAra.Server.Contracts.DeviceType;

namespace OpenAstroAra.Test {

    /// <summary>#1689 story 22 — while the dome follows the mount, a sequence light waits for the dome to
    /// reach the scope before the exposure starts; a dark does not look through the slit and starts at once.</summary>
    [TestFixture]
    [Category("IO")] // #1265 — real disk + loopback HTTP
    public class CameraDomeWaitTest {

        private string _dir = string.Empty;

        [SetUp]
        public void SetUp() => _dir = Directory.CreateTempSubdirectory("ara-domewait-").FullName;

        [TearDown]
        public void TearDown() {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        [Test]
        public async Task A_light_waits_for_the_following_dome_and_a_dark_does_not() {
            await using var box = ScriptedAlpacaDevice.Start(path =>
                path.EndsWith("/imageready", StringComparison.Ordinal) ? "true"
                : path.EndsWith("/imagearray", StringComparison.Ordinal) ? "[[100,200],[300,400]],\"Type\":2,\"Rank\":2"
                : null);
            var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var follower = new Mock<IDomeFollower>();
            follower.SetupGet(f => f.IsFollowing).Returns(true);
            follower.Setup(f => f.WaitForDomeSynchronization(It.IsAny<CancellationToken>())).Returns(arrived.Task);
            using var svc = new CameraService(frames: new Mock<IFrameRepository>().Object, fallbackFramesDir: _dir,
                domeFollower: () => follower.Object);
            await svc.ConnectAsync(new ConnectRequestDto(new DiscoveredDeviceDto(
                UniqueId: "Camera-under-test", Name: "Sim Camera", Type: DeviceType.Camera,
                HostName: box.BaseUri.Host, IpAddress: box.BaseUri.Host, IpPort: box.BaseUri.Port,
                AlpacaDeviceNumber: 0, UseHttps: false)), null, CancellationToken.None);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while ((await svc.GetAsync(CancellationToken.None)) is not { State: EquipmentConnectionState.Connected, Capabilities: not null }) {
                Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "camera never connected");
                await Task.Delay(50);
            }
            int Exposures() => box.Puts.Count(p => p.Path.EndsWith("/startexposure", StringComparison.Ordinal));

            await svc.CaptureImage(new CaptureSequence { ExposureTime = 0.01, ImageType = "DARK" },
                CancellationToken.None, null, "M42").WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(Exposures(), Is.EqualTo(1), "the dark started while the dome was still turning");
            follower.Verify(f => f.WaitForDomeSynchronization(It.IsAny<CancellationToken>()), Times.Never);

            var light = svc.CaptureImage(new CaptureSequence { ExposureTime = 0.01, ImageType = "LIGHT" },
                CancellationToken.None, null, "M42");
            await Task.Delay(300);
            Assert.That(light.IsCompleted, Is.False, "the light waits for the dome");
            Assert.That(Exposures(), Is.EqualTo(1), "no light exposure before the dome arrives");

            arrived.SetResult();
            await light.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(Exposures(), Is.EqualTo(2));
            await svc.FinishPendingSavesAsync();
        }
    }
}
