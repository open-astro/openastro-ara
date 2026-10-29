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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Alpaca;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CaptureSequence = OpenAstroAra.Equipment.Model.CaptureSequence;
using DeviceType = OpenAstroAra.Server.Contracts.DeviceType;

namespace OpenAstroAra.Test {

    /// <summary>
    /// A capture writes only the exposure settings the camera doesn't already hold. On AlpacaBridge a
    /// read costs ~1 ms and a binning or subframe write ~47 ms (the SDK reconfigures), so rewriting all
    /// of them cost ~0.3 s per frame on a CM4 for a sequence whose settings never change.
    /// </summary>
    [TestFixture]
    [Category("IO")] // #1265 — real disk + loopback HTTP
    public class CameraExposureSettingsTest {

        private static readonly string[] SettingEndpoints =
            ["/binx", "/biny", "/gain", "/offset", "/startx", "/starty", "/numx", "/numy"];
        private static readonly string[] BinGainNumY = ["/binx", "/gain", "/numy"];
        private static readonly string[] OffsetOnly = ["/offset"];

        private string _dir = string.Empty;

        [SetUp]
        public void SetUp() => _dir = Directory.CreateTempSubdirectory("ara-settings-").FullName;

        [TearDown]
        public void TearDown() {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        // The camera's current settings; a test overrides entries to make the camera differ.
        private static Func<string, string?> Camera(Dictionary<string, string> state) => path => {
            foreach (var (endpoint, value) in state) {
                if (path.EndsWith(endpoint, StringComparison.Ordinal)) {
                    return value;
                }
            }
            return path.EndsWith("/imageready", StringComparison.Ordinal) ? "true"
                : path.EndsWith("/imagearray", StringComparison.Ordinal) ? "[[100,200],[300,400]],\"Type\":2,\"Rank\":2"
                : null;
        };

        private static Dictionary<string, string> Matching() => new() {
            ["/cameraxsize"] = "4", ["/cameraysize"] = "4",
            ["/binx"] = "1", ["/biny"] = "1", ["/gain"] = "100", ["/offset"] = "50",
            ["/startx"] = "0", ["/starty"] = "0", ["/numx"] = "4", ["/numy"] = "4",
        };

        private async Task<string[]> SettingWritesOfOneCaptureAsync(Dictionary<string, string> state) {
            await using var box = ScriptedAlpacaDevice.Start(Camera(state));
            using var svc = new CameraService(frames: new Mock<IFrameRepository>().Object, fallbackFramesDir: _dir);
            await svc.ConnectAsync(new ConnectRequestDto(new DiscoveredDeviceDto(
                UniqueId: "Camera-under-test", Name: "Sim Camera", Type: DeviceType.Camera,
                HostName: box.BaseUri.Host, IpAddress: box.BaseUri.Host, IpPort: box.BaseUri.Port,
                AlpacaDeviceNumber: 0, UseHttps: false)), null, CancellationToken.None);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while ((await svc.GetAsync(CancellationToken.None)) is not { State: EquipmentConnectionState.Connected, Capabilities: not null }) {
                Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "camera never connected");
                await Task.Delay(50);
            }
            var before = box.Puts.Count;

            await svc.CaptureImage(new CaptureSequence { ExposureTime = 0.01, ImageType = "DARK", Gain = 100, Offset = 50 },
                CancellationToken.None, null, "M42");
            await svc.FinishPendingSavesAsync();

            return box.Puts.Skip(before).Select(p => p.Path[p.Path.LastIndexOf('/')..])
                .Where(SettingEndpoints.Contains).ToArray();
        }

        [Test]
        public async Task Settings_the_camera_already_holds_are_not_written_again() {
            Assert.That(await SettingWritesOfOneCaptureAsync(Matching()), Is.Empty);
        }

        [Test]
        public async Task Only_the_settings_that_differ_are_written() {
            var state = Matching();
            state["/binx"] = "2";
            state["/gain"] = "0";
            state["/numy"] = "2";
            Assert.That(await SettingWritesOfOneCaptureAsync(state), Is.EquivalentTo(BinGainNumY));
        }

        [Test]
        public async Task A_setting_that_cannot_be_read_is_written() {
            var state = Matching();
            state["/offset"] = ScriptedAlpacaDevice.Error("Offset read failed");
            Assert.That(await SettingWritesOfOneCaptureAsync(state), Is.EquivalentTo(OffsetOnly));
        }
    }
}
