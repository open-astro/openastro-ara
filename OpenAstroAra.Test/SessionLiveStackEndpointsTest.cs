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
using OpenAstroAra.Stretch;
using System;

namespace OpenAstroAra.Test {

    /// <summary>↑ara#1787: status mapping and headers of the Session Live Stack routes, on the extracted handlers
    /// (the repo's endpoint-test pattern, as LiveStackEndpointsTest).</summary>
    [TestFixture]
    public class SessionLiveStackEndpointsTest {

        private static int StatusOf(IResult r) => r switch {
            ProblemHttpResult p => p.StatusCode,
            NoContent => StatusCodes.Status204NoContent,
            Accepted => StatusCodes.Status202Accepted,
            Ok<SessionLiveStackStatusDto> => StatusCodes.Status200OK,
            FileContentHttpResult => StatusCodes.Status200OK,
            _ => throw new InvalidOperationException(r.GetType().Name),
        };

        // Falsified by: a handler without the capability cast throws on a camera service that has no Session Live Stack.
        [Test]
        public void A_camera_service_without_the_capability_gets_501_on_every_route() {
            var svc = new Mock<ICameraService>().Object;
            Assert.Multiple(() => {
                Assert.That(StatusOf(SessionLiveStackEndpoints.Start(svc)), Is.EqualTo(501));
                Assert.That(StatusOf(SessionLiveStackEndpoints.Reset(svc)), Is.EqualTo(501));
                Assert.That(StatusOf(SessionLiveStackEndpoints.Stop(svc)), Is.EqualTo(501));
                Assert.That(StatusOf(SessionLiveStackEndpoints.Status(svc)), Is.EqualTo(501));
                Assert.That(StatusOf(SessionLiveStackEndpoints.Frame(svc, new DefaultHttpContext())), Is.EqualTo(501));
            });
        }

        // Falsified by: Start answering 200/204 (the contract is 202), or any of the others answering differently.
        [Test]
        public void Start_is_202_stop_and_reset_are_204_status_is_200_and_the_frame_is_204_until_a_light_is_stacked() {
            using var svc = new CameraService();
            Assert.Multiple(() => {
                Assert.That(StatusOf(SessionLiveStackEndpoints.Start(svc)), Is.EqualTo(202));
                Assert.That(StatusOf(SessionLiveStackEndpoints.Status(svc)), Is.EqualTo(200));
                Assert.That(StatusOf(SessionLiveStackEndpoints.Frame(svc, new DefaultHttpContext())), Is.EqualTo(204));
                Assert.That(StatusOf(SessionLiveStackEndpoints.Reset(svc)), Is.EqualTo(204));
                Assert.That(StatusOf(SessionLiveStackEndpoints.Stop(svc)), Is.EqualTo(204));
            });
        }

        // Falsified by: dropping a header, or a cacheable frame, breaks a poller's change detection.
        [Test]
        public void The_frame_is_a_no_store_jpeg_with_the_sequence_and_light_count() {
            using var svc = new CameraService();
            svc.SessionLiveStack.Enable(0);
            svc.SessionLiveStack.AddColour(OscScene.Mosaic(1), OscScene.W, OscScene.H, BayerPattern.RGGB);
            var http = new DefaultHttpContext();
            var r = SessionLiveStackEndpoints.Frame(svc, http);
            Assert.Multiple(() => {
                Assert.That(StatusOf(r), Is.EqualTo(200));
                Assert.That(((FileContentHttpResult)r).ContentType, Is.EqualTo("image/jpeg"));
                Assert.That(http.Response.Headers["X-Stack-Seq"].ToString(), Is.EqualTo("1"));
                Assert.That(http.Response.Headers["X-Stack-Lights"].ToString(), Is.EqualTo("1"));
                Assert.That(http.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            });
        }
    }
}