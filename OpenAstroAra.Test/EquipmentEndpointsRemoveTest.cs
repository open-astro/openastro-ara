#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using OpenAstroAra.Server;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    // #1127 — status mapping of the card's Remove/Connect handlers, on the extracted handlers with
    // mocked services (the repo's endpoint-test pattern, see EquipmentEndpointsMoveAxisTest).
    [TestFixture]
    public class EquipmentEndpointsRemoveTest {

        private static readonly NullLogger<Program> Logger = NullLogger<Program>.Instance;

        private static int StatusOf(IResult r) => r switch {
            ProblemHttpResult p => p.StatusCode,
            NoContent => StatusCodes.Status204NoContent,
            NotFound => StatusCodes.Status404NotFound,
            Accepted<OperationAcceptedDto> => StatusCodes.Status202Accepted,
            _ => throw new InvalidOperationException(r.GetType().Name),
        };

        private static OperationAcceptedDto Accepted() =>
            new(Guid.NewGuid(), "switch.connect", DateTimeOffset.UtcNow, null);

        // ─── RemoveDeviceAsync (the single-instance DELETE) ───────────────────────────────────

        [Test]
        public async Task Remove_drops_the_device_and_forgets_the_remembered_entry() {
            var store = new Mock<IEquipmentSelectionStore>();
            var result = await EquipmentEndpoints.RemoveDeviceAsync(() => Task.FromResult(true), store.Object, DeviceType.Camera, Logger, CancellationToken.None);
            Assert.That(StatusOf(result), Is.EqualTo(StatusCodes.Status204NoContent));
            store.Verify(s => s.ForgetAsync(DeviceType.Camera, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task Remove_of_a_live_device_is_409_and_keeps_the_remembered_entry() {
            var store = new Mock<IEquipmentSelectionStore>();
            var result = await EquipmentEndpoints.RemoveDeviceAsync(
                () => throw new InvalidOperationException("the camera is connected — disconnect it before removing it"),
                store.Object, DeviceType.Camera, Logger, CancellationToken.None);
            Assert.That(StatusOf(result), Is.EqualTo(StatusCodes.Status409Conflict));
            store.Verify(s => s.ForgetAsync(It.IsAny<DeviceType>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task Remove_racing_the_daemons_shutdown_is_a_server_fault_not_a_409() {
            // ObjectDisposedException derives from InvalidOperationException; a disposed service is
            // not a "disconnect first", it is the daemon going away — let the 5xx pipeline have it.
            var store = new Mock<IEquipmentSelectionStore>();
            await Assert.ThatAsync(() => EquipmentEndpoints.RemoveDeviceAsync(
                () => throw new ObjectDisposedException(nameof(CameraService)),
                store.Object, DeviceType.Camera, Logger, CancellationToken.None), Throws.TypeOf<ObjectDisposedException>());
        }

        [Test]
        public async Task Remove_is_still_204_when_the_store_forget_fails_on_IO() {
            var store = new Mock<IEquipmentSelectionStore>();
            store.Setup(s => s.ForgetAsync(DeviceType.Focuser, It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("read-only"));
            var result = await EquipmentEndpoints.RemoveDeviceAsync(() => Task.FromResult(true), store.Object, DeviceType.Focuser, Logger, CancellationToken.None);
            Assert.That(StatusOf(result), Is.EqualTo(StatusCodes.Status204NoContent), "the device is already gone from the live service");
        }

        // ─── RemoveSwitchAsync (DELETE /switch/{id}) ──────────────────────────────────────────

        [Test]
        public async Task Switch_remove_forgets_the_remembered_switch_by_id() {
            var svc = new Mock<ISwitchService>();
            svc.Setup(s => s.RemoveAsync("sw-1", It.IsAny<CancellationToken>())).ReturnsAsync(true);
            var store = new Mock<IEquipmentSelectionStore>();
            var result = await EquipmentEndpoints.RemoveSwitchAsync("sw-1", svc.Object, store.Object, Logger, CancellationToken.None);
            Assert.That(StatusOf(result), Is.EqualTo(StatusCodes.Status204NoContent));
            store.Verify(s => s.ForgetSwitchAsync("sw-1", It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task Switch_remove_of_an_unknown_id_is_404() {
            var svc = new Mock<ISwitchService>();
            svc.Setup(s => s.RemoveAsync("ghost", It.IsAny<CancellationToken>())).ReturnsAsync(false);
            var store = new Mock<IEquipmentSelectionStore>();
            Assert.That(StatusOf(await EquipmentEndpoints.RemoveSwitchAsync("ghost", svc.Object, store.Object, Logger, CancellationToken.None)),
                Is.EqualTo(StatusCodes.Status404NotFound));
            store.Verify(s => s.ForgetSwitchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task Switch_remove_of_a_live_switch_is_409() {
            var svc = new Mock<ISwitchService>();
            svc.Setup(s => s.RemoveAsync("sw-1", It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("the switch is connected — disconnect it before removing it"));
            var result = await EquipmentEndpoints.RemoveSwitchAsync("sw-1", svc.Object, new Mock<IEquipmentSelectionStore>().Object, Logger, CancellationToken.None);
            Assert.That(StatusOf(result), Is.EqualTo(StatusCodes.Status409Conflict));
        }

        [Test]
        public async Task Switch_remove_racing_the_daemons_shutdown_is_a_server_fault_not_a_409() {
            var svc = new Mock<ISwitchService>();
            svc.Setup(s => s.RemoveAsync("sw-1", It.IsAny<CancellationToken>())).ThrowsAsync(new ObjectDisposedException(nameof(SwitchService)));
            await Assert.ThatAsync(() => EquipmentEndpoints.RemoveSwitchAsync(
                "sw-1", svc.Object, new Mock<IEquipmentSelectionStore>().Object, Logger, CancellationToken.None), Throws.TypeOf<ObjectDisposedException>());
        }

        // ─── ConnectSwitchAsync (POST /switch/{id}/connect) ───────────────────────────────────

        [Test]
        public async Task Switch_connect_of_a_known_switch_is_202_with_the_accepted_body() {
            var svc = new Mock<ISwitchService>();
            svc.Setup(s => s.ReconnectAsync("sw-1", "key-1", It.IsAny<CancellationToken>())).ReturnsAsync(Accepted());
            var result = await EquipmentEndpoints.ConnectSwitchAsync("sw-1", "key-1", svc.Object, CancellationToken.None);
            Assert.That(StatusOf(result), Is.EqualTo(StatusCodes.Status202Accepted));
            Assert.That(((Accepted<OperationAcceptedDto>)result).Value?.OperationType, Is.EqualTo("switch.connect"));
        }

        [Test]
        public async Task Switch_connect_of_an_unknown_id_is_404() {
            var svc = new Mock<ISwitchService>();
            svc.Setup(s => s.ReconnectAsync("ghost", null, It.IsAny<CancellationToken>())).ReturnsAsync((OperationAcceptedDto?)null);
            Assert.That(StatusOf(await EquipmentEndpoints.ConnectSwitchAsync("ghost", null, svc.Object, CancellationToken.None)),
                Is.EqualTo(StatusCodes.Status404NotFound));
        }
    }
}
