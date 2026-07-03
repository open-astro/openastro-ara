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
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §14e — live end-to-end test for the capture path: discovers a Camera from a running ASCOM
    /// OmniSim, connects, takes a short exposure through <see cref="CameraService.StartExposureAsync"/>,
    /// and asserts the REAL pipeline outcome — a frame row in the §28 catalog, a §72 FITS file on
    /// disk with plausible dimensions, and preview bytes served by the §65 stretch pipeline. Runs in
    /// the <c>alpaca-sim-integration</c> CI job.
    /// </summary>
    [TestFixture]
    [Category("Integration")]
    public class CameraConnectIntegrationTest {

        private static readonly Uri ManagementProbeUri = new("http://127.0.0.1:32323/management/apiversions");
        private const int MaxDiscoveryAttempts = 6;

        private string _profileDir = string.Empty;
        private SqliteAraDatabase _db = null!;

        [OneTimeSetUp]
        public async Task OneTimeSetUp() {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            try {
                using var resp = await http.GetAsync(ManagementProbeUri).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) {
                    Assert.Ignore($"OmniSim management API returned {(int)resp.StatusCode} on :32323 — skipping live Camera test.");
                }
            } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) {
                Assert.Ignore("No ASCOM OmniSim answering on :32323 — start one (or run the alpaca-sim-integration CI job) to exercise this test.");
            }
            _profileDir = Path.Combine(Path.GetTempPath(), $"oara-camera-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_profileDir);
            _db = new SqliteAraDatabase(_profileDir, logger: null);
            await _db.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        }

        [OneTimeTearDown]
        public void OneTimeTearDown() {
            try { Directory.Delete(_profileDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        [Test]
        public async Task Connect_exposes_and_the_frame_lands_in_catalog_fits_and_preview() {
            var device = await DiscoverAsync().ConfigureAwait(false);
            Assert.That(device, Is.Not.Null, "no Camera discovered from the running OmniSim");

            var store = new InMemoryProfileStore();
            // Point the §29 save directory at the temp profile dir so the capture writes there.
            store.PutStorageSettings(new StorageSettingsDto(
                SaveDirectory: Path.Combine(_profileDir, "frames"),
                FileFormat: "fits", Compression: "off", FilenameTemplate: "$$DATETIME$$"));
            var frames = new SqliteFrameRepository(_db, store);
            using var svc = new CameraService(logger: null, frames, store, fallbackFramesDir: Path.Combine(_profileDir, "fallback"));

            await svc.ConnectAsync(new ConnectRequestDto(device!), idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            var connected = await PollUntilAsync(svc, d => d.State != EquipmentConnectionState.Connecting).ConfigureAwait(false);
            Assert.That(connected!.State, Is.EqualTo(EquipmentConnectionState.Connected));
            var withCaps = await PollUntilAsync(svc, d => d.Capabilities is not null).ConfigureAwait(false);
            Assert.That(withCaps!.Capabilities!.SensorWidth, Is.GreaterThan(0), "sensor size should be read from the sim");

            try {
                // §28 widening: sub-second on purpose — this is the ONLY test that drives
                // RegisterFrameAsync, and 0.5 used to be rounded up to a catalogued 1 s.
                var response = await svc.StartExposureAsync(new ExposureRequestDto(ExposureSec: 0.5, Gain: null), idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
                Assert.That(response.FrameId, Is.Not.Empty);
                var frameId = Guid.Parse(response.FrameId);
                Assert.That(response.PreviewUrl.ToString(), Does.Contain(response.FrameId));

                // The capture pipeline (expose → download → FITS → catalog) runs in the background;
                // poll the catalog the way WILMA polls the preview URL.
                FrameDto? frame = null;
                for (var i = 0; i < 240 && frame is null; i++) {
                    frame = await frames.GetAsync(frameId, CancellationToken.None).ConfigureAwait(false);
                    if (frame is null) {
                        await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                    }
                }
                Assert.That(frame, Is.Not.Null, "the captured frame never appeared in the catalog");
                Assert.That(frame!.ExposureSeconds, Is.EqualTo(0.5), "the real capture path must catalog sub-second exposures verbatim (§28)");
                Assert.That(frame.Gain, Is.Null, "a request without gain must catalog NULL, not a sentinel (§28)");
                Assert.That(frame.Width, Is.GreaterThan(0));
                Assert.That(frame.Height, Is.GreaterThan(0));
                Assert.That(File.Exists(frame.FilePath), Is.True, "the FITS file should exist on disk");
                Assert.That(frame.FileSizeBytes, Is.GreaterThan(0));

                // End-to-end: the §65 preview pipeline must serve the new frame.
                var preview = await frames.GetPreviewAsync(frameId, new FramePreviewRequestDto(StretchPalette: "auto_stf", BlackPoint: null, MidtonePoint: null, WhitePoint: null, MaxDimensionPx: 512, ApplyDebayer: false), CancellationToken.None).ConfigureAwait(false);
                Assert.That(preview, Is.Not.Null, "preview pipeline should serve the captured frame");
                Assert.That(preview!.Value.Bytes.Length, Is.GreaterThan(0));
            } finally {
                await svc.DisconnectAsync(idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            }
            var disconnected = await PollUntilAsync(svc, d => d.State == EquipmentConnectionState.Disconnected).ConfigureAwait(false);
            Assert.That(disconnected!.State, Is.EqualTo(EquipmentConnectionState.Disconnected));
        }

        private static async Task<DiscoveredDeviceDto?> DiscoverAsync() {
            var discovery = new AlpacaEquipmentDiscoveryService();
            for (var attempt = 1; attempt <= MaxDiscoveryAttempts; attempt++) {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var found = await discovery.DiscoverAsync(DeviceType.Camera, forceRefresh: true, cts.Token).ConfigureAwait(false);
                if (found.Count > 0) {
                    return found[0];
                }
                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }
            return null;
        }

        private static async Task<CameraDto?> PollUntilAsync(CameraService svc, Func<CameraDto, bool> predicate) {
            for (var i = 0; i < 60; i++) {
                var dto = await svc.GetAsync(CancellationToken.None).ConfigureAwait(false);
                if (dto is not null && predicate(dto)) {
                    return dto;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
            }
            return await svc.GetAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}
