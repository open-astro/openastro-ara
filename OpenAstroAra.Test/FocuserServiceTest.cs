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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// Sim-free unit coverage for <see cref="FocuserService"/> — the fourth real device service
    /// (control action: Move). Mirrors the prior suites; the live happy path (read state + move)
    /// lives in the <c>[Category("Integration")]</c> companion test.
    /// </summary>
    [TestFixture]
    public class FocuserServiceTest {

        private static async Task<(FocuserService Svc, FakeFocuserDevice Focuser)> ConnectFakeAsync(params string[] actions) {
            var focuser = new FakeFocuserDevice();
            focuser.Actions.AddRange(actions);
            var svc = new FocuserService { RefreshPeriod = TimeSpan.FromMilliseconds(50), DeviceFactory = _ => focuser };
            var device = new DiscoveredDeviceDto("focuser-under-test", "Bench Focuser", DeviceType.Focuser, "127.0.0.1", "127.0.0.1", 1, 0, false);
            await svc.ConnectAsync(new ConnectRequestDto(device), null, CancellationToken.None);
            await PollAsync(svc, d => d.Capabilities is not null);
            return (svc, focuser);
        }

        private static async Task<FocuserDto?> PollAsync(FocuserService svc, Func<FocuserDto, bool> done) {
            for (var i = 0; i < 200; i++) {
                var dto = await svc.GetAsync(CancellationToken.None);
                if (dto is not null && done(dto)) {
                    return dto;
                }
                await Task.Delay(25);
            }
            return await svc.GetAsync(CancellationToken.None);
        }

        [Test]
        public async Task Capabilities_offer_zero_and_calibration_only_when_the_driver_lists_the_actions() {
            var (none, _) = await ConnectFakeAsync();
            using (none) {
                var caps = (await none.GetAsync(CancellationToken.None))!.Capabilities!;
                Assert.That((caps.CanSetZero, caps.CanCalibrate), Is.EqualTo((false, false)));
            }
            var (all, _) = await ConnectFakeAsync("setzero", "Calibrate", "AbortCalibration", "CalibrationStatus");
            using (all) {
                var caps = (await all.GetAsync(CancellationToken.None))!.Capabilities!;
                Assert.That((caps.CanSetZero, caps.CanCalibrate), Is.EqualTo((true, true)), "names match case-insensitively");
            }
        }

        [Test]
        public async Task SetZeroAsync_runs_the_drivers_SetZero_and_the_position_reads_zero() {
            var (svc, focuser) = await ConnectFakeAsync(FocuserService.SetZeroAction);
            using (svc) {
                Assert.That((await svc.GetAsync(CancellationToken.None))!.Runtime.Position, Is.EqualTo(500));
                var op = await svc.SetZeroAsync("k1", CancellationToken.None);
                Assert.That(op.OperationType, Is.EqualTo("focuser.set_zero"));
                Assert.That(focuser.ActionCalls, Is.EqualTo(new[] { FocuserService.SetZeroAction }));
                Assert.That((await PollAsync(svc, d => d.Runtime.Position == 0))!.Runtime.Position, Is.EqualTo(0));
            }
        }

        [Test]
        public async Task SetZeroAsync_after_a_compensated_reversal_reads_zero_not_the_standing_offset() {
            var store = new InMemoryProfileStore();
            store.PutAutofocusSettings(new AutofocusSettingsDto("hfr_v_curve", 4, 100, 2, 1, "L", false, 1.0, 10, 0, false, true, StepSizeAuto: false,
                BacklashInSteps: 30, BacklashOutSteps: 40));
            using var focuser = new FakeFocuserDevice();
            focuser.MoveLands = true;
            focuser.Actions.Add(FocuserService.SetZeroAction);
            using var svc = new FocuserService(profiles: store) { RefreshPeriod = TimeSpan.FromMilliseconds(50), DeviceFactory = _ => focuser };
            var device = new DiscoveredDeviceDto("focuser-under-test", "Bench Focuser", DeviceType.Focuser, "127.0.0.1", "127.0.0.1", 1, 0, false);
            await svc.ConnectAsync(new ConnectRequestDto(device), null, CancellationToken.None);
            await PollAsync(svc, d => d.Capabilities is not null);
            await svc.MoveFocuser(800, CancellationToken.None);
            Assert.That(await svc.MoveFocuser(600, CancellationToken.None), Is.EqualTo(600), "the reversal went 30 further in: hardware 570, offset -30");

            await svc.SetZeroAsync(null, CancellationToken.None);

            Assert.That((await PollAsync(svc, d => d.Runtime.Position == 0))!.Runtime.Position, Is.EqualTo(0), "the zero leaves no standing offset");
        }

        [Test]
        public async Task The_action_routes_answer_409_when_refused_not_500() {
            // Not connected, then connected without the actions: each refusal is a state conflict.
            using (var idle = new FocuserService()) {
                Assert.That(await StatusOfAsync(() => idle.SetZeroAsync(null, CancellationToken.None)), Is.EqualTo(StatusCodes.Status409Conflict));
            }
            var (svc, _) = await ConnectFakeAsync();
            using (svc) {
                Assert.That(await StatusOfAsync(() => svc.SetZeroAsync(null, CancellationToken.None)), Is.EqualTo(StatusCodes.Status409Conflict));
                Assert.That(await StatusOfAsync(() => svc.CalibrateAsync(null, CancellationToken.None)), Is.EqualTo(StatusCodes.Status409Conflict));
                Assert.That(await StatusOfAsync(() => svc.AbortCalibrationAsync(null, CancellationToken.None)), Is.EqualTo(StatusCodes.Status409Conflict));
            }
        }

        private static async Task<int?> StatusOfAsync(Func<Task<OperationAcceptedDto>> action) =>
            await EquipmentEndpoints.FocuserActionAsync(action) switch {
                ProblemHttpResult p => p.StatusCode,
                Accepted<OperationAcceptedDto> => StatusCodes.Status202Accepted,
                var r => throw new InvalidOperationException(r.GetType().Name),
            };

        [Test]
        public async Task SetZeroAsync_is_refused_when_the_driver_does_not_offer_it() {
            var (svc, focuser) = await ConnectFakeAsync();
            using (svc) {
                await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SetZeroAsync(null, CancellationToken.None));
                Assert.That(focuser.ActionCalls, Is.Empty);
            }
        }

        [Test]
        public async Task A_running_calibration_reads_calibrating_refuses_a_zero_and_can_be_stopped() {
            var (svc, focuser) = await ConnectFakeAsync(FocuserService.SetZeroAction, FocuserService.CalibrateAction,
                FocuserService.AbortCalibrationAction, FocuserService.CalibrationStatusAction);
            using (svc) {
                Assert.That((await PollAsync(svc, d => d.Runtime.Calibration is not null))!.Runtime.Calibration, Is.EqualTo("not_calibrated"));
                await svc.CalibrateAsync(null, CancellationToken.None);
                focuser.CalibrationStatus = "Calibrating";
                Assert.That((await PollAsync(svc, d => d.Runtime.Calibration == "calibrating"))!.Runtime.Calibration, Is.EqualTo("calibrating"));
                await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SetZeroAsync(null, CancellationToken.None));
                await svc.AbortCalibrationAsync(null, CancellationToken.None);
                Assert.That(focuser.ActionCalls, Is.EqualTo(new[] { FocuserService.CalibrateAction, FocuserService.AbortCalibrationAction }));
            }
        }

        [Test]
        public async Task HaltAsync_when_not_connected_throws_InvalidOperation() {
            using var svc = new FocuserService();
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.HaltAsync(null, CancellationToken.None));
        }

        [Test]
        public async Task HaltAsync_after_Dispose_throws_ObjectDisposedException() {
            var svc = new FocuserService();
            svc.Dispose();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => svc.HaltAsync(null, CancellationToken.None));
        }

        [Test]
        public async Task ForgetAsync_with_no_device_retained_returns_false() {
            using var svc = new FocuserService();
            Assert.That(await svc.ForgetAsync(CancellationToken.None), Is.False);
        }

        [Test]
        public async Task ForgetAsync_after_a_disconnect_drops_the_retained_device() {
            using var svc = new FocuserService();
            var dead = new DiscoveredDeviceDto("uid", "U", DeviceType.Focuser, "127.0.0.1", "127.0.0.1", 1, 0, false);
            await svc.ConnectAsync(new ConnectRequestDto(dead), null, CancellationToken.None);
            // Disconnect supersedes the in-flight connect (generation bump), so no settle poll is needed.
            await svc.DisconnectAsync(null, CancellationToken.None);
            var retained = await svc.GetAsync(CancellationToken.None);
            Assert.That(retained, Is.Not.Null, "retained after disconnect");
            Assert.That(retained!.State, Is.EqualTo(EquipmentConnectionState.Disconnected));

            Assert.That(await svc.ForgetAsync(CancellationToken.None), Is.True);

            // The card's Remove: the status GET reads 404 again, as before any device was selected.
            Assert.That(await svc.GetAsync(CancellationToken.None), Is.Null);
        }

        [Test]
        public async Task ForgetAsync_publishes_a_removed_state_changed_and_clears_the_retained_record() {
            // #1127 — a second open client drops its card on the event instead of waiting for its
            // next poll, and the reconnector's retained-device fallback sees nothing afterwards.
            var ws = new Mock<IWsBroadcaster>();
            var events = new List<(string Type, JsonElement Payload)>();
            ws.Setup(w => w.PublishAsync(It.IsAny<OpenAstroAra.Server.Contracts.WsEvents.WsEvent>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
                .Callback<OpenAstroAra.Server.Contracts.WsEvents.WsEvent, JsonElement, CancellationToken>((t, p, _) => { lock (events) { events.Add((t, p.Clone())); } })
                .Returns(Task.CompletedTask);
            using var svc = new FocuserService(events: new EquipmentEventPublisher(ws.Object));
            var dead = new DiscoveredDeviceDto("uid", "U", DeviceType.Focuser, "127.0.0.1", "127.0.0.1", 1, 0, false);
            await svc.ConnectAsync(new ConnectRequestDto(dead), null, CancellationToken.None);
            await svc.DisconnectAsync(null, CancellationToken.None);
            Assert.That(((IRetainedDeviceSource)svc).RetainedDevice?.UniqueId, Is.EqualTo("uid"), "retained while disconnected");

            Assert.That(await svc.ForgetAsync(CancellationToken.None), Is.True);

            Assert.That(((IRetainedDeviceSource)svc).RetainedDevice, Is.Null);
            (string Type, JsonElement Payload) removed;
            lock (events) {
                removed = events.Last(e => e.Type == WsEventCatalog.EquipmentStateChanged);
            }
            Assert.That(removed.Payload.GetProperty("removed").GetBoolean(), Is.True, "the last state_changed is the removal");
            Assert.That(removed.Payload.GetProperty("device_id").GetString(), Is.EqualTo("uid"));
        }

        [Test]
        public async Task ForgetAsync_while_connecting_throws_InvalidOperation() {
            using var svc = new FocuserService();
            var dead = new DiscoveredDeviceDto("uid", "U", DeviceType.Focuser, "127.0.0.1", "127.0.0.1", 1, 0, false);
            await svc.ConnectAsync(new ConnectRequestDto(dead), null, CancellationToken.None);
            // ConnectAsync sets Connecting synchronously; the live states refuse a removal (→ 409).
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ForgetAsync(CancellationToken.None));
            await svc.DisconnectAsync(null, CancellationToken.None); // supersede the dead connect before dispose
        }

        [Test]
        public async Task GetAsync_is_null_before_any_device_is_selected() {
            using var svc = new FocuserService();
            Assert.That(await svc.GetAsync(CancellationToken.None), Is.Null);
        }

        [Test]
        public async Task ConnectAsync_to_an_unreachable_device_ends_in_Error() {
            using var svc = new FocuserService { DeviceFactory = _ => throw new InvalidOperationException("connection refused") };
            var dead = new DiscoveredDeviceDto(
                "unit-test-uid", "Unreachable Focuser", DeviceType.Focuser,
                "127.0.0.1", "127.0.0.1", 1, 0, false);

            await svc.ConnectAsync(new ConnectRequestDto(dead), null, CancellationToken.None);

            var dto = await PollUntilNotConnectingAsync(svc);
            Assert.That(dto, Is.Not.Null, "connect never left the Connecting state");
            Assert.That(dto!.State, Is.EqualTo(EquipmentConnectionState.Error));
            Assert.That(dto.Capabilities, Is.Null, "no capabilities while not Connected");
        }

        [Test]
        public async Task DisconnectAsync_after_a_failed_connect_returns_to_Disconnected() {
            using var svc = new FocuserService { DeviceFactory = _ => throw new InvalidOperationException("connection refused") };
            var dead = new DiscoveredDeviceDto(
                "uid", "Unreachable", DeviceType.Focuser, "127.0.0.1", "127.0.0.1", 1, 0, false);

            await svc.ConnectAsync(new ConnectRequestDto(dead), null, CancellationToken.None);
            await PollUntilNotConnectingAsync(svc);

            await svc.DisconnectAsync(null, CancellationToken.None);
            var dto = await svc.GetAsync(CancellationToken.None);
            Assert.That(dto!.State, Is.EqualTo(EquipmentConnectionState.Disconnected));
        }

        [Test]
        public void MoveAsync_when_not_connected_throws_InvalidOperation() {
            using var svc = new FocuserService();
            Assert.Throws<InvalidOperationException>(
                () => { _ = svc.MoveAsync(new FocuserMoveRequestDto(1000), null, CancellationToken.None); });
        }

        [Test]
        public void ConnectAsync_after_Dispose_throws_ObjectDisposedException() {
            var svc = new FocuserService();
            svc.Dispose();
            var dead = new DiscoveredDeviceDto("uid", "D", DeviceType.Focuser, "127.0.0.1", "127.0.0.1", 1, 0, false);
            Assert.Throws<ObjectDisposedException>(
                () => { _ = svc.ConnectAsync(new ConnectRequestDto(dead), null, CancellationToken.None); });
        }

        [Test]
        public void DisconnectAsync_after_Dispose_throws_ObjectDisposedException() {
            var svc = new FocuserService();
            svc.Dispose();
            Assert.Throws<ObjectDisposedException>(
                () => { _ = svc.DisconnectAsync(null, CancellationToken.None); });
        }

        [Test]
        public async Task GetAsync_after_Dispose_throws_ObjectDisposedException() {
            var svc = new FocuserService();
            svc.Dispose();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => svc.GetAsync(CancellationToken.None));
        }

        [Test]
        public void MoveAsync_after_Dispose_throws_ObjectDisposedException() {
            var svc = new FocuserService();
            svc.Dispose();
            Assert.Throws<ObjectDisposedException>(
                () => { _ = svc.MoveAsync(new FocuserMoveRequestDto(1000), null, CancellationToken.None); });
        }

        // Direct coverage of the MoveAsync range-validation logic (pure function, no sim needed).
        [Test]
        public void IsTargetOutOfRange_absolute_focuser_enforces_zero_to_max() {
            var caps = new FocuserCapabilitiesDto(MinPosition: 0, MaxPosition: 10000, StepSizeUm: 1, CanTempComp: false, AbsoluteFocuser: true);
            Assert.That(FocuserService.IsTargetOutOfRange(caps, 5000), Is.False);
            Assert.That(FocuserService.IsTargetOutOfRange(caps, 0), Is.False);
            Assert.That(FocuserService.IsTargetOutOfRange(caps, 10000), Is.False);
            Assert.That(FocuserService.IsTargetOutOfRange(caps, 10001), Is.True, "above max");
            Assert.That(FocuserService.IsTargetOutOfRange(caps, -1), Is.True, "below 0 (absolute)");
        }

        [Test]
        public void IsTargetOutOfRange_relative_focuser_allows_negative_offsets() {
            var caps = new FocuserCapabilitiesDto(MinPosition: -10000, MaxPosition: 10000, StepSizeUm: 1, CanTempComp: false, AbsoluteFocuser: false);
            Assert.That(FocuserService.IsTargetOutOfRange(caps, -5000), Is.False, "backward step is valid");
            Assert.That(FocuserService.IsTargetOutOfRange(caps, -10000), Is.False);
            Assert.That(FocuserService.IsTargetOutOfRange(caps, -10001), Is.True, "below -max");
            Assert.That(FocuserService.IsTargetOutOfRange(caps, 10001), Is.True, "above max");
        }

        [Test]
        public void IsTargetOutOfRange_unknown_or_unlimited_max_skips_validation() {
            // caps null (loading window) or MaxPosition == 0 (driver "unlimited") -> device validates.
            Assert.That(FocuserService.IsTargetOutOfRange(null, -99999), Is.False);
            var unlimited = new FocuserCapabilitiesDto(MinPosition: 0, MaxPosition: 0, StepSizeUm: 1, CanTempComp: false, AbsoluteFocuser: false);
            Assert.That(FocuserService.IsTargetOutOfRange(unlimited, -99999), Is.False);
            Assert.That(FocuserService.IsTargetOutOfRange(unlimited, 99999), Is.False);
        }

        private static async Task<FocuserDto?> PollUntilNotConnectingAsync(FocuserService svc) {
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