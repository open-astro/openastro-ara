#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using ASCOM.Common.DeviceInterfaces;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// Sim-free unit coverage for the §14e <see cref="CameraService"/> (the capture-path head).
    /// The live capture pipeline is exercised by the <c>[Category("Integration")]</c> companion
    /// test against OmniSim; here we cover the not-connected/disposed REST contracts, the
    /// validation ordering, and the pure helpers (ImageArray conversion, state mapping,
    /// frames-dir resolution).
    /// </summary>
    [TestFixture]
    public class CameraServiceTest {

        [Test]
        public async Task GetAsync_returns_null_before_any_device_was_selected() {
            using var svc = new CameraService();
            Assert.That(await svc.GetAsync(CancellationToken.None), Is.Null);
        }

        [Test]
        public void StartExposureAsync_when_not_connected_throws_InvalidOperation() {
            using var svc = new CameraService();
            Assert.Throws<InvalidOperationException>(
                () => { _ = svc.StartExposureAsync(new ExposureRequestDto(1.0, Gain: null), null, CancellationToken.None); });
        }

        [Test]
        public void StartExposureAsync_rejects_nonpositive_exposure_before_connected_check() {
            using var svc = new CameraService();
            // Argument range validates BEFORE the connected check (services-wide ordering), so a
            // bad exposure on a disconnected service reports the argument problem, not the state.
            Assert.Throws<ArgumentOutOfRangeException>(
                () => { _ = svc.StartExposureAsync(new ExposureRequestDto(0, Gain: null), null, CancellationToken.None); });
            Assert.Throws<ArgumentOutOfRangeException>(
                () => { _ = svc.StartExposureAsync(new ExposureRequestDto(-2, Gain: null), null, CancellationToken.None); });
        }

        [Test]
        public void StartExposureAsync_rejects_invalid_binning() {
            using var svc = new CameraService();
            Assert.Throws<ArgumentOutOfRangeException>(
                () => { _ = svc.StartExposureAsync(new ExposureRequestDto(1.0, Gain: null, BinX: 0), null, CancellationToken.None); });
        }

        [Test]
        public void AbortExposureAsync_when_not_connected_throws_InvalidOperation() {
            using var svc = new CameraService();
            // Async method: the guard surfaces on the returned Task.
            Assert.ThrowsAsync<InvalidOperationException>(() => svc.AbortExposureAsync(CancellationToken.None));
        }

        [Test]
        public void Ops_after_Dispose_throw_ObjectDisposed() {
            var svc = new CameraService();
            svc.Dispose();
            Assert.Throws<ObjectDisposedException>(
                () => { _ = svc.GetAsync(CancellationToken.None); });
            Assert.Throws<ObjectDisposedException>(
                () => { _ = svc.StartExposureAsync(new ExposureRequestDto(1.0, Gain: null), null, CancellationToken.None); });
        }

        [Test]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1814:Prefer jagged arrays over multidimensional",
            Justification = "The API under test consumes ASCOM's ImageArray, which IS a multidimensional int[x,y] by spec.")]
        public void ConvertImageArray_transposes_column_major_to_row_major_and_clamps() {
            // ASCOM ImageArray is [x, y]; FITS wants row-major rows of width.
            var arr = new int[2, 3]; // width=2, height=3
            arr[0, 0] = 10; arr[1, 0] = 20;
            arr[0, 1] = 30; arr[1, 1] = 40;
            arr[0, 2] = -5; arr[1, 2] = 70000; // clamp both ends

            var (pixels, width, height) = CameraService.ConvertImageArray(arr);

            Assert.That(width, Is.EqualTo(2));
            Assert.That(height, Is.EqualTo(3));
            Assert.That(pixels, Is.EqualTo(new ushort[] { 10, 20, 30, 40, 0, 65535 }));
        }

        [Test]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1814:Prefer jagged arrays over multidimensional",
            Justification = "The API under test consumes ASCOM's ImageArray, which IS a multidimensional array by spec (3-axis = color).")]
        public void ConvertImageArray_rejects_color_and_unknown_payloads() {
            Assert.Throws<InvalidOperationException>(() => CameraService.ConvertImageArray(new int[2, 2, 3]));
            Assert.Throws<InvalidOperationException>(() => CameraService.ConvertImageArray(null));
            Assert.Throws<InvalidOperationException>(() => CameraService.ConvertImageArray("not an array"));
        }

        [Test]
        public void MapState_covers_the_ascom_camera_states() {
            Assert.That(CameraService.MapState(CameraState.Idle), Is.EqualTo("idle"));
            Assert.That(CameraService.MapState(CameraState.Waiting), Is.EqualTo("exposing"));
            Assert.That(CameraService.MapState(CameraState.Exposing), Is.EqualTo("exposing"));
            Assert.That(CameraService.MapState(CameraState.Reading), Is.EqualTo("downloading"));
            Assert.That(CameraState.Download, Is.Not.EqualTo(CameraState.Reading));
            Assert.That(CameraService.MapState(CameraState.Download), Is.EqualTo("downloading"));
            Assert.That(CameraService.MapState(CameraState.Error), Is.EqualTo("error"));
        }

        [Test]
        public void ResolveFramesDir_falls_back_when_no_store_is_configured() {
            var fallback = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ara-frames-{Guid.NewGuid():N}");
            using var svc = new CameraService(fallbackFramesDir: fallback);
            var dir = svc.ResolveFramesDir();
            Assert.That(dir, Does.StartWith(fallback));
            Assert.That(dir, Does.EndWith("manual"));
        }
    }
}
