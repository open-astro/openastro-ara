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
using ASCOM.Common.DeviceInterfaces;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// Sim-free unit coverage for <see cref="FlatDeviceService"/> — the seventh real device service
    /// (cover + calibrator-light control). Mirrors the prior suites; the live happy path lives in
    /// the <c>[Category("Integration")]</c> companion test.
    /// </summary>
    [TestFixture]
    public class FlatDeviceServiceTest {

        [Test]
        public async Task GetAsync_is_null_before_any_device_is_selected() {
            using var svc = new FlatDeviceService();
            Assert.That(await svc.GetAsync(CancellationToken.None), Is.Null);
        }

        [Test]
        public async Task ConnectAsync_to_an_unreachable_device_ends_in_Error() {
            using var svc = new FlatDeviceService();
            var dead = new DiscoveredDeviceDto(
                "unit-test-uid", "Unreachable Flat", DeviceType.FlatDevice, "127.0.0.1", "127.0.0.1", 1, 0, false);
            await svc.ConnectAsync(new ConnectRequestDto(dead), null, CancellationToken.None);
            var dto = await PollUntilNotConnectingAsync(svc);
            Assert.That(dto, Is.Not.Null);
            Assert.That(dto!.State, Is.EqualTo(EquipmentConnectionState.Error));
        }

        [Test]
        public async Task DisconnectAsync_after_a_failed_connect_returns_to_Disconnected() {
            using var svc = new FlatDeviceService();
            var dead = new DiscoveredDeviceDto("uid", "U", DeviceType.FlatDevice, "127.0.0.1", "127.0.0.1", 1, 0, false);
            await svc.ConnectAsync(new ConnectRequestDto(dead), null, CancellationToken.None);
            await PollUntilNotConnectingAsync(svc);
            await svc.DisconnectAsync(null, CancellationToken.None);
            var dto = await svc.GetAsync(CancellationToken.None);
            Assert.That(dto!.State, Is.EqualTo(EquipmentConnectionState.Disconnected));
        }

        [Test]
        public void ApplyFlatPanelAsync_when_not_connected_throws_InvalidOperation() {
            using var svc = new FlatDeviceService();
            Assert.Throws<InvalidOperationException>(
                () => { _ = svc.ApplyFlatPanelAsync(new FlatPanelRequestDto(OpenCover: true), null, CancellationToken.None); });
        }

        [Test]
        public void ApplyFlatPanelAsync_with_negative_brightness_throws_ArgumentOutOfRange() {
            using var svc = new FlatDeviceService();
            // Negative brightness is out of range regardless of the (unknown) max, so this is
            // reachable without a sim and fires before the connected check.
            Assert.Throws<ArgumentOutOfRangeException>(
                () => { _ = svc.ApplyFlatPanelAsync(new FlatPanelRequestDto(Brightness: -1), null, CancellationToken.None); });
        }

        [Test]
        public void IsBrightnessOutOfRange_enforces_zero_to_max_when_known() {
            Assert.That(FlatDeviceService.IsBrightnessOutOfRange(100, 0), Is.False);
            Assert.That(FlatDeviceService.IsBrightnessOutOfRange(100, 100), Is.False);
            Assert.That(FlatDeviceService.IsBrightnessOutOfRange(100, 101), Is.True, "above max");
            Assert.That(FlatDeviceService.IsBrightnessOutOfRange(100, -1), Is.True);
            // Unknown (null) or zero max -> only negatives rejected; the device validates the upper.
            Assert.That(FlatDeviceService.IsBrightnessOutOfRange(null, 9999), Is.False);
            Assert.That(FlatDeviceService.IsBrightnessOutOfRange(null, -1), Is.True);
            Assert.That(FlatDeviceService.IsBrightnessOutOfRange(0, 9999), Is.False);
        }

        // ─── Runtime mapping (§37.4 capability + warming rules) ──────────────────────────

        [Test]
        public void MapRuntime_reports_the_devices_max_brightness() {
            var dto = FlatDeviceService.MapRuntime(
                CoverStatus.Closed, CalibratorStatus.Ready, brightness: 120, maxBrightness: 255);
            Assert.Multiple(() => {
                Assert.That(dto.MaxBrightness, Is.EqualTo(255));
                Assert.That(dto.Brightness, Is.EqualTo(120));
                Assert.That(dto.State, Is.EqualTo("light_on"));
            });
        }

        [Test]
        public void MapRuntime_reports_zero_max_until_it_has_been_read() {
            // A device whose MaxBrightness hasn't been read yet must not masquerade as a
            // real range — the client disables its brightness control on 0 rather than
            // commanding levels the device would reject.
            var dto = FlatDeviceService.MapRuntime(
                CoverStatus.Closed, CalibratorStatus.Off, brightness: 0, maxBrightness: null);
            Assert.That(dto.MaxBrightness, Is.Zero);
        }

        [Test]
        public void MapRuntime_hides_the_cover_only_when_the_device_says_NotPresent() {
            var bare = FlatDeviceService.MapRuntime(
                CoverStatus.NotPresent, CalibratorStatus.Ready, 10, 255);
            var unknown = FlatDeviceService.MapRuntime(
                CoverStatus.Unknown, CalibratorStatus.Ready, 10, 255);
            Assert.Multiple(() => {
                Assert.That(bare.HasCover, Is.False, "a bare light panel has no cover row");
                // Unknown is an unsupported/failed READ, not proof of absence — keep the
                // control rather than hiding one that works.
                Assert.That(unknown.HasCover, Is.True);
            });
        }

        [Test]
        public void MapRuntime_hides_the_light_only_when_the_device_says_NotPresent() {
            var dustCover = FlatDeviceService.MapRuntime(
                CoverStatus.Closed, CalibratorStatus.NotPresent, 0, 0);
            var unknown = FlatDeviceService.MapRuntime(
                CoverStatus.Closed, CalibratorStatus.Unknown, 0, 0);
            Assert.Multiple(() => {
                Assert.That(dustCover.HasCalibrator, Is.False);
                Assert.That(unknown.HasCalibrator, Is.True);
            });
        }

        [Test]
        public void MapRuntime_reports_a_warming_calibrator_as_warming_not_off() {
            // An EL panel ramping to the commanded level is NotReady. Reporting that as a
            // plain "off" reads to the client as a FAILED command; LightWarming keeps the
            // in-flight command distinguishable from a refusal.
            var dto = FlatDeviceService.MapRuntime(
                CoverStatus.Closed, CalibratorStatus.NotReady, brightness: 200, maxBrightness: 255);
            Assert.Multiple(() => {
                Assert.That(dto.LightWarming, Is.True);
                Assert.That(dto.LightOn, Is.False, "not on until the device says Ready");
                Assert.That(dto.Brightness, Is.Zero, "brightness only counts while the light is on");
            });
        }

        [Test]
        public void MapRuntime_maps_the_cover_states_to_their_tokens() {
            Assert.Multiple(() => {
                Assert.That(FlatDeviceService.MapRuntime(CoverStatus.Moving, CalibratorStatus.Off, 0, 255).State,
                    Is.EqualTo("cover_moving"));
                Assert.That(FlatDeviceService.MapRuntime(CoverStatus.Open, CalibratorStatus.Off, 0, 255).State,
                    Is.EqualTo("cover_open"));
                Assert.That(FlatDeviceService.MapRuntime(CoverStatus.Closed, CalibratorStatus.Off, 0, 255).State,
                    Is.EqualTo("cover_closed"));
                Assert.That(FlatDeviceService.MapRuntime(CoverStatus.Error, CalibratorStatus.Off, 0, 255).State,
                    Is.EqualTo("error"));
                Assert.That(FlatDeviceService.MapRuntime(CoverStatus.Closed, CalibratorStatus.Error, 0, 255).State,
                    Is.EqualTo("error"));
            });
        }

        // ─── Cover-settle before a light change ──────────────────────────────────────────

        [Test]
        public void NeedsCoverSettle_covers_a_STANDALONE_light_command() {
            // The regression this guards: a light command sent while the cover is still
            // travelling from an EARLIER request. Panels reject a calibrator change
            // mid-motion ("A cover open/close operation is already in progress") and the
            // caller already holds its 202, so the failure is invisible — it must wait the
            // cover out, not only when the same request also moves the cover.
            Assert.Multiple(() => {
                Assert.That(FlatDeviceService.NeedsCoverSettle(new FlatPanelRequestDto(LightOn: true)), Is.True);
                Assert.That(FlatDeviceService.NeedsCoverSettle(new FlatPanelRequestDto(Brightness: 128)), Is.True);
                Assert.That(FlatDeviceService.NeedsCoverSettle(new FlatPanelRequestDto(Brightness: 0)), Is.True);
                Assert.That(
                    FlatDeviceService.NeedsCoverSettle(new FlatPanelRequestDto(OpenCover: true, LightOn: true)),
                    Is.True);
                // A cover-only move has nothing to wait for — it IS the motion.
                Assert.That(FlatDeviceService.NeedsCoverSettle(new FlatPanelRequestDto(OpenCover: true)), Is.False);
            });
        }

        [Test]
        public void WaitForCoverSettle_returns_as_soon_as_the_cover_stops() {
            var readings = new Queue<CoverStatus>(new[] {
                CoverStatus.Moving, CoverStatus.Moving, CoverStatus.Moving, CoverStatus.Open,
            });
            var sleeps = 0;
            var settled = FlatDeviceService.WaitForCoverSettle(
                () => readings.Dequeue(), () => sleeps++, FlatDeviceService.MaxSettlePolls);
            Assert.Multiple(() => {
                Assert.That(settled, Is.True);
                Assert.That(sleeps, Is.EqualTo(3), "one sleep per moving reading, none after it rests");
            });
        }

        [Test]
        public void WaitForCoverSettle_gives_up_at_the_budget_rather_than_hanging() {
            // A cover that never stops (jammed, or a driver stuck reporting Moving) must not
            // pin the fire-and-forget apply thread forever — best effort, bounded.
            var sleeps = 0;
            var settled = FlatDeviceService.WaitForCoverSettle(
                () => CoverStatus.Moving, () => sleeps++, maxPolls: 5);
            Assert.Multiple(() => {
                Assert.That(settled, Is.False);
                Assert.That(sleeps, Is.EqualTo(5));
            });
        }

        [Test]
        public void WaitForCoverSettle_budget_spans_a_real_covers_travel() {
            // 200 polls x 200ms = ~40s. Real motorised covers take 10-30s end to end; the
            // original 6s budget expired mid-travel and the light op then failed.
            Assert.That(FlatDeviceService.MaxSettlePolls, Is.GreaterThanOrEqualTo(150));
        }

        [Test]
        public void ConnectAsync_after_Dispose_throws_ObjectDisposedException() {
            var svc = new FlatDeviceService();
            svc.Dispose();
            var dead = new DiscoveredDeviceDto("uid", "D", DeviceType.FlatDevice, "127.0.0.1", "127.0.0.1", 1, 0, false);
            Assert.Throws<ObjectDisposedException>(
                () => { _ = svc.ConnectAsync(new ConnectRequestDto(dead), null, CancellationToken.None); });
        }

        [Test]
        public void DisconnectAsync_after_Dispose_throws_ObjectDisposedException() {
            var svc = new FlatDeviceService();
            svc.Dispose();
            Assert.Throws<ObjectDisposedException>(
                () => { _ = svc.DisconnectAsync(null, CancellationToken.None); });
        }

        [Test]
        public void GetAsync_after_Dispose_throws_ObjectDisposedException() {
            var svc = new FlatDeviceService();
            svc.Dispose();
            Assert.ThrowsAsync<ObjectDisposedException>(() => svc.GetAsync(CancellationToken.None));
        }

        [Test]
        public void ApplyFlatPanelAsync_after_Dispose_throws_ObjectDisposedException() {
            var svc = new FlatDeviceService();
            svc.Dispose();
            Assert.Throws<ObjectDisposedException>(
                () => { _ = svc.ApplyFlatPanelAsync(new FlatPanelRequestDto(OpenCover: true), null, CancellationToken.None); });
        }

        private static async Task<FlatDeviceDto?> PollUntilNotConnectingAsync(FlatDeviceService svc) {
            for (var i = 0; i < 150; i++) {
                var dto = await svc.GetAsync(CancellationToken.None);
                if (dto is not null && dto.State != EquipmentConnectionState.Connecting) {
                    return dto;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }
            return await svc.GetAsync(CancellationToken.None);
        }
    }
}
