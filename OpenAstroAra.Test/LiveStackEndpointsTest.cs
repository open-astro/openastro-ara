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
using Moq;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>↑ara#1169: status mapping and headers of the live-stack routes, on the extracted handlers (the repo's
    /// endpoint-test pattern), plus the 409 cases that need a running Live View session.</summary>
    [TestFixture]
    public class LiveStackEndpointsTest {

        private static int StatusOf(IResult r) => r switch {
            ProblemHttpResult p => p.StatusCode,
            NoContent => StatusCodes.Status204NoContent,
            Accepted => StatusCodes.Status202Accepted,
            Ok<LiveStackStatusDto> => StatusCodes.Status200OK,
            FileContentHttpResult => StatusCodes.Status200OK,
            _ => throw new InvalidOperationException(r.GetType().Name),
        };

        private static CameraCapabilitiesDto Caps(int w, int h, string? bayer) => new(
            w, h, 3.8, false, true, false, 0, 100, 0, 100, 1, 4, 1, 4, 0.001, 600, BayerPattern: bayer);

        // Falsified by: an endpoint that calls the service without the cast guard throws on a camera service that
        // has no live stacking; the contract is a 501 not-implemented problem on every route.
        [Test]
        public void A_camera_service_without_live_stacking_gets_501_on_every_route() {
            var svc = new Mock<ICameraService>().Object; // a camera service with no live stacking
            var http = new DefaultHttpContext();
            Assert.Multiple(() => {
                Assert.That(StatusOf(LiveStackEndpoints.Start(svc)), Is.EqualTo(501));
                Assert.That(StatusOf(LiveStackEndpoints.Reset(svc)), Is.EqualTo(501));
                Assert.That(StatusOf(LiveStackEndpoints.Stop(svc)), Is.EqualTo(501));
                Assert.That(StatusOf(LiveStackEndpoints.Status(svc)), Is.EqualTo(501));
                Assert.That(StatusOf(LiveStackEndpoints.Frame(svc, http)), Is.EqualTo(501));
            });
        }

        // Falsified by: mapping InvalidOperationException to 500 (or letting it escape) instead of 409.
        [Test]
        public void Start_without_a_running_live_view_is_409() {
            using var svc = new CameraService();
            Assert.That(StatusOf(LiveStackEndpoints.Start(svc)), Is.EqualTo(StatusCodes.Status409Conflict));
        }

        // Falsified by: a Frame handler that answers 200 with an empty body, or 404, before the first stacked frame.
        [Test]
        public void Frame_is_204_while_nothing_is_stacked_and_reset_and_stop_are_204() {
            using var svc = new CameraService();
            Assert.Multiple(() => {
                Assert.That(StatusOf(LiveStackEndpoints.Frame(svc, new DefaultHttpContext())), Is.EqualTo(204));
                Assert.That(StatusOf(LiveStackEndpoints.Reset(svc)), Is.EqualTo(204));
                Assert.That(StatusOf(LiveStackEndpoints.Stop(svc)), Is.EqualTo(204));
                Assert.That(StatusOf(LiveStackEndpoints.Status(svc)), Is.EqualTo(200));
            });
        }

        // Falsified by: dropping a header, or sending the frame cacheable, breaks a poller's change detection.
        [Test]
        public void Frame_sends_the_jpeg_with_the_stack_headers_and_no_store() {
            using var svc = new CameraService();
            svc.LiveStack.Enable(3);
            Assert.That(svc.LiveStack.Add(LiveStackEngineLimitsTest.Field(1), LiveStackEngineLimitsTest.W, LiveStackEngineLimitsTest.H, 3).Outcome,
                Is.EqualTo(OpenAstroAra.Server.Services.LiveStack.StackAddOutcome.Reference));
            var http = new DefaultHttpContext();
            var r = LiveStackEndpoints.Frame(svc, http);
            Assert.That(StatusOf(r), Is.EqualTo(200));
            Assert.That(http.Response.Headers["X-Stack-Seq"].ToString(), Is.EqualTo("1"));
            Assert.That(http.Response.Headers["X-Live-Session"].ToString(), Is.EqualTo("3"));
            Assert.That(http.Response.Headers["X-Stack-Frames"].ToString(), Is.EqualTo("1"));
            Assert.That(http.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        }

        // Falsified by: a Start that does not read the session's Bayer pattern lets an OSC 1x1 session stack a
        // mosaic (Enabled true, nothing registers); the contract is 409.
        [Test]
        public async Task Start_on_an_osc_session_at_1x1_is_409_but_a_binned_one_is_accepted() {
            using var svc = new CameraService();
            svc.WithLiveViewTestClientForTest(() => new FakeCameraDevice { ExposureFails = true });
            svc.WithCapabilitiesForTest(Caps(1000, 800, "RGGB"));
            try {
                await svc.StartLiveViewAsync(new LiveViewStartRequestDto(1.0, BinX: 1, BinY: 1), CancellationToken.None);
                Assert.That(StatusOf(LiveStackEndpoints.Start(svc)), Is.EqualTo(StatusCodes.Status409Conflict));
                await svc.StopLiveViewAsync();
                await svc.StartLiveViewAsync(new LiveViewStartRequestDto(1.0, BinX: 2, BinY: 2), CancellationToken.None);
                Assert.That(StatusOf(LiveStackEndpoints.Start(svc)), Is.EqualTo(StatusCodes.Status202Accepted));
            } finally {
                await svc.StopLiveViewAsync();
            }
        }

        // Falsified by: no size check at start lets a 24 MP session enable a stack whose first frame is refused.
        [Test]
        public async Task Start_with_a_known_frame_size_over_the_cap_is_409() {
            using var svc = new CameraService();
            svc.WithLiveViewTestClientForTest(() => new FakeCameraDevice { ExposureFails = true });
            svc.WithCapabilitiesForTest(Caps(6000, 4000, null));
            try {
                await svc.StartLiveViewAsync(new LiveViewStartRequestDto(1.0, BinX: 1, BinY: 1), CancellationToken.None);
                Assert.That(StatusOf(LiveStackEndpoints.Start(svc)), Is.EqualTo(StatusCodes.Status409Conflict));
                await svc.StopLiveViewAsync();
                await svc.StartLiveViewAsync(new LiveViewStartRequestDto(1.0, BinX: 2, BinY: 2), CancellationToken.None);
                Assert.That(StatusOf(LiveStackEndpoints.Start(svc)), Is.EqualTo(StatusCodes.Status202Accepted));
            } finally {
                await svc.StopLiveViewAsync();
            }
        }
    }
}
