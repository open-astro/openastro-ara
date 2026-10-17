#region "copyright"
/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/
#endregion "copyright"

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1364 item 5 — <c>GET /equipment/guider/history</c> through real HTTP binding: the documented
    /// snake-case <c>max_points</c> reaches the service (the review found only <c>?maxPoints=</c> did), and
    /// a request without it asks for the default 400.</summary>
    [TestFixture]
    [Category("IO")] // #1265 — loopback HTTP: not part of the quick unit run
    public class EquipmentEndpointsGuiderHistoryTest {

        private static async Task<int?> AskedWithAsync(string query) {
            var live = new Mock<IGuiderLiveService>();
            int? asked = null;
            live.Setup(l => l.GetHistoryAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Callback<int, CancellationToken>((n, _) => asked = n)
                .ReturnsAsync(new GuiderHistoryDto(true, Array.Empty<GuiderHistoryStepDto>()));
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(live.Object);
            await using var app = builder.Build();
            // The daemon's own handler, so its binding attributes are what is under test.
            Delegate handler = EquipmentEndpoints.GetGuiderHistoryAsync;
            app.MapGet("/history", handler);
            await app.StartAsync().ConfigureAwait(false);
            try {
                var address = app.Urls.First();
                using var http = new HttpClient { BaseAddress = new Uri(address) };
                using var response = await http.GetAsync(new Uri("/history" + query, UriKind.Relative)).ConfigureAwait(false);
                Assert.That((int)response.StatusCode, Is.EqualTo(200));
            } finally {
                await app.StopAsync().ConfigureAwait(false);
            }
            return asked;
        }

        [Test]
        public async Task The_documented_max_points_reaches_the_service() {
            Assert.That(await AskedWithAsync("?max_points=50").ConfigureAwait(false), Is.EqualTo(50));
        }

        [Test]
        public async Task Without_max_points_the_default_400_is_asked() {
            Assert.That(await AskedWithAsync("").ConfigureAwait(false), Is.EqualTo(400));
        }
    }
}
