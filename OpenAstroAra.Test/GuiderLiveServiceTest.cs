#region "copyright"
/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/
#endregion "copyright"

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using OpenAstroAra.Server.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1364 — the live guide-camera view without a guider: the disconnected contract, which
    /// is what the Live tab shows before Connect, and the action surface's refusals.</summary>
    [TestFixture]
    public class GuiderLiveServiceTest {

        private static GuiderService NewGuiderService() =>
            new(new HeadlessProfileService(),
                new GuiderRecoveryCoordinator(Mock.Of<IGuiderProcessSupervisor>(), Mock.Of<INotificationService>(),
                    Mock.Of<IDiagnosticsService>(), NullLogger<GuiderRecoveryCoordinator>.Instance),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());

        private sealed class NoFrames : IPolarAlignFrameFetcher {
            public Task FetchAsync(string host, int rpcPort, string filename, string destinationPath, CancellationToken ct) => Task.CompletedTask;
            public Task<byte[]> FetchLiveFrameJpegAsync(string host, int rpcPort, CancellationToken ct) => throw new System.Net.Http.HttpRequestException("404");
        }

        [Test]
        public async Task Without_a_guider_the_view_is_disconnected_and_has_no_frame() {
            using var guider = NewGuiderService();
            var live = new GuiderLiveService(guider, new NoFrames());
            var view = await live.GetAsync(CancellationToken.None);
            Assert.That(view.Connected, Is.False);
            Assert.That(view.HasFrame, Is.False);
            Assert.That(view.Stars, Is.Empty);
            Assert.That(view.LockX, Is.Null);
            Assert.That(await live.GetFrameJpegAsync(CancellationToken.None), Is.Null, "204, never an error");
        }

        [Test]
        public void Actions_without_a_guider_are_refused_as_not_connected() {
            using var guider = NewGuiderService();
            var live = new GuiderLiveService(guider, new NoFrames());
            Assert.That(() => live.LoopAsync(CancellationToken.None), Throws.InvalidOperationException);
            Assert.That(() => live.StopAsync(CancellationToken.None), Throws.InvalidOperationException);
            Assert.That(() => live.DeselectStarAsync(CancellationToken.None), Throws.InvalidOperationException);
            Assert.That(() => live.SelectStarAsync(100, 200, CancellationToken.None), Throws.InvalidOperationException);
        }

        [Test]
        public void A_tap_outside_the_frame_is_a_bad_request_before_any_guider_is_asked() {
            using var guider = NewGuiderService();
            var live = new GuiderLiveService(guider, new NoFrames());
            Assert.That(() => live.SelectStarAsync(-1, 10, CancellationToken.None), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => live.SelectStarAsync(double.NaN, 10, CancellationToken.None), Throws.TypeOf<ArgumentOutOfRangeException>());
        }

        [Test]
        public async Task The_endpoint_helper_maps_the_three_refusals() {
            var notConnected = await OpenAstroAra.Server.Endpoints.EquipmentEndpoints.GuiderLiveActionAsync(() => throw new InvalidOperationException("guider is not connected"));
            var badPosition = await OpenAstroAra.Server.Endpoints.EquipmentEndpoints.GuiderLiveActionAsync(() => throw new ArgumentOutOfRangeException("x"));
            var refused = await OpenAstroAra.Server.Endpoints.EquipmentEndpoints.GuiderLiveActionAsync(
                () => throw new OpenAstroAra.Equipment.Equipment.MyGuider.PHD2.GuiderRpcException("set_lock_position", 1, "no image"));
            var ok = await OpenAstroAra.Server.Endpoints.EquipmentEndpoints.GuiderLiveActionAsync(() => Task.CompletedTask);
            Assert.That(StatusOf(notConnected), Is.EqualTo(409));
            Assert.That(StatusOf(badPosition), Is.EqualTo(400));
            Assert.That(StatusOf(refused), Is.EqualTo(422));
            Assert.That(StatusOf(ok), Is.EqualTo(204));
        }

        private static int? StatusOf(Microsoft.AspNetCore.Http.IResult result) =>
            (result as Microsoft.AspNetCore.Http.IStatusCodeHttpResult)?.StatusCode;
    }
}
