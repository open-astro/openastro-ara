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
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// Sim-free unit coverage for the §64 Live View surface on <see cref="CameraService"/>: the
    /// not-connected/validation guards and the initial idle state. The actual short-exposure render
    /// loop is exercised against OmniSim in the integration companion (a connected device is needed
    /// to produce a frame).
    /// </summary>
    [TestFixture]
    public class CameraServiceLiveViewTest {

        [Test]
        public void StartLiveViewAsync_when_not_connected_throws_InvalidOperation() {
            using var svc = new CameraService();
            Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.StartLiveViewAsync(new LiveViewStartRequestDto(1.0), CancellationToken.None));
        }

        [Test]
        public void StartLiveViewAsync_rejects_nonpositive_exposure_before_connected_check() {
            using var svc = new CameraService();
            Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => svc.StartLiveViewAsync(new LiveViewStartRequestDto(0), CancellationToken.None));
            Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => svc.StartLiveViewAsync(new LiveViewStartRequestDto(-1.5), CancellationToken.None));
        }

        [Test]
        public void StartLiveViewAsync_rejects_exposure_over_the_cap() {
            using var svc = new CameraService();
            Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => svc.StartLiveViewAsync(new LiveViewStartRequestDto(120), CancellationToken.None));
        }

        [Test]
        public void StartLiveViewAsync_accepts_the_exposure_cap_boundary() {
            using var svc = new CameraService();
            // 15.0 is the inclusive cap: it must pass validation and fall through to the
            // connection check (InvalidOperation), NOT be rejected as out-of-range.
            Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.StartLiveViewAsync(new LiveViewStartRequestDto(15.0), CancellationToken.None));
        }

        [Test]
        public void StartLiveViewAsync_rejects_invalid_binning() {
            using var svc = new CameraService();
            Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => svc.StartLiveViewAsync(new LiveViewStartRequestDto(1.0, BinX: 0), CancellationToken.None));
            Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => svc.StartLiveViewAsync(new LiveViewStartRequestDto(1.0, BinY: 0), CancellationToken.None));
            // Absurdly large binning is rejected up front (cap is 16) rather than failing per-frame.
            Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => svc.StartLiveViewAsync(new LiveViewStartRequestDto(1.0, BinX: 9999), CancellationToken.None));
        }

        [Test]
        public async Task StopLiveViewAsync_when_not_running_is_a_noop() {
            using var svc = new CameraService();
            await svc.StopLiveViewAsync(CancellationToken.None); // must not throw
            Assert.That(svc.GetLiveViewStatus().Active, Is.False);
        }

        [Test]
        public void GetLiveViewStatus_is_idle_before_any_start() {
            using var svc = new CameraService();
            var status = svc.GetLiveViewStatus();
            Assert.Multiple(() => {
                Assert.That(status.Active, Is.False);
                Assert.That(status.FrameSeq, Is.EqualTo(0));
                Assert.That(status.Width, Is.Null);
                Assert.That(status.LastFrameAtUtc, Is.Null);
            });
            Assert.That(svc.GetLiveViewFrame(), Is.Null);
        }

        [Test]
        public void Placeholder_live_view_surface_is_inert() {
            var svc = new PlaceholderCameraService();
            Assert.DoesNotThrowAsync(() => svc.StartLiveViewAsync(new LiveViewStartRequestDto(1.0), CancellationToken.None));
            Assert.DoesNotThrowAsync(() => svc.StopLiveViewAsync(CancellationToken.None));
            Assert.That(svc.GetLiveViewStatus().Active, Is.False);
            Assert.That(svc.GetLiveViewFrame(), Is.Null);
        }
    }
}
