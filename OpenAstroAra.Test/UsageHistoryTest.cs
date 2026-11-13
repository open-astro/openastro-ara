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
using NUnit.Framework;
using OpenAstroAra.Server;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

// The service is a BackgroundService that these tests never start; there is nothing to dispose.
#pragma warning disable CA2000

namespace OpenAstroAra.Test {

    /// <summary>#1582 — the rig's 12 h usage ring and its route.</summary>
    [TestFixture]
    public class UsageHistoryTest {

        private static readonly DateTimeOffset T0 = new(2026, 11, 13, 20, 0, 0, TimeSpan.Zero);

        private static (UsageHistoryService Svc, Action<TimeSpan, long?> Advance) Make(int processors = 4) {
            var now = T0;
            TimeSpan cpu = TimeSpan.Zero;
            long? free = 100;
            var svc = new UsageHistoryService(
                () => new UsageReading(free, 1000, cpu, 5_000),
                () => now, processors);
            return (svc, (dt, f) => { now += dt; cpu += dt * 2; free = f; });
        }

        [Test]
        public void An_empty_buffer_returns_no_samples_and_the_interval() {
            var (svc, _) = Make();
            var h = svc.Snapshot();
            Assert.That(h.Samples, Is.Empty);
            Assert.That(h.IntervalSeconds, Is.EqualTo(15));
            Assert.That(h.ProcessorCount, Is.EqualTo(4));
        }

        [Test]
        public void Cpu_is_a_share_of_all_cores_and_null_on_the_first_tick() {
            var (svc, advance) = Make(processors: 4);
            svc.Tick();
            advance(TimeSpan.FromSeconds(15), 90); // 2 cores busy of 4
            svc.Tick();
            var s = svc.Snapshot().Samples;
            Assert.That(s[0].CpuPercent, Is.Null);
            Assert.That(s[1].CpuPercent, Is.EqualTo(50).Within(0.001));
            Assert.That(s[1].StorageFreeBytes, Is.EqualTo(90));
            Assert.That(s[1].MemoryBytes, Is.EqualTo(5_000));
        }

        [Test]
        public void The_ring_wraps_at_12_hours_and_drops_the_oldest() {
            var (svc, advance) = Make();
            for (var i = 0; i < UsageHistoryService.Capacity + 5; i++) {
                svc.Tick();
                advance(TimeSpan.FromSeconds(15), 100);
            }
            var s = svc.Snapshot().Samples;
            Assert.That(UsageHistoryService.Capacity, Is.EqualTo(2880));
            Assert.That(s, Has.Count.EqualTo(2880));
            Assert.That(s[0].AtUtc, Is.EqualTo(T0.AddSeconds(5 * 15)));
        }

        [Test]
        public void Since_returns_only_strictly_newer_samples() {
            var (svc, advance) = Make();
            for (var i = 0; i < 4; i++) {
                svc.Tick();
                advance(TimeSpan.FromSeconds(15), 100);
            }
            var after = svc.Snapshot(T0.AddSeconds(15)).Samples;
            Assert.That(after.Select(x => x.AtUtc), Is.EqualTo(new[] { T0.AddSeconds(30), T0.AddSeconds(45) }));
        }

        [Test]
        public void A_failed_read_stores_null_never_zero() {
            var fail = false;
            var svc = new UsageHistoryService(
                () => fail ? new UsageReading(null, null, null, null) : new UsageReading(1, 2, TimeSpan.Zero, 3),
                () => T0);
            svc.Tick();
            fail = true;
            svc.Tick();
            var s = svc.Snapshot().Samples[1];
            Assert.That(s.StorageFreeBytes, Is.Null);
            Assert.That(s.StorageTotalBytes, Is.Null);
            Assert.That(s.CpuPercent, Is.Null);
            Assert.That(s.MemoryBytes, Is.Null);
        }

        [Test]
        public void A_throwing_source_is_a_gap_not_a_dead_sampler() {
            var svc = new UsageHistoryService(() => throw new InvalidOperationException("x"), () => T0);
            Assert.DoesNotThrow(svc.Tick);
            Assert.That(svc.Snapshot().Samples.Single().MemoryBytes, Is.Null);
        }

        [Test]
        public void A_stats_panel_read_between_ticks_does_not_change_the_sampler_cpu() {
            var now = T0;
            var cpu = TimeSpan.Zero;
            var svc = new UsageHistoryService(() => new UsageReading(1, 2, cpu, 3), () => now, processors: 1);
            var panel = new ServerInternalStateService(() => now, startedUtc: T0);
            svc.Tick();
            now = T0.AddSeconds(15); cpu = TimeSpan.FromSeconds(3);
            panel.Sample();
            now = T0.AddSeconds(30); cpu = TimeSpan.FromSeconds(6);
            panel.Sample();
            svc.Tick();
            Assert.That(svc.Snapshot().Samples[1].CpuPercent, Is.EqualTo(20).Within(0.001), "6 s of CPU over the sampler's own 30 s window");
        }

        [Test]
        public async Task The_route_returns_snake_cased_samples_with_no_store_and_honours_since() {
            var (svc, advance) = Make();
            svc.Tick();
            advance(TimeSpan.FromSeconds(15), 90);
            svc.Tick();
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(svc);
            builder.Services.AddSingleton<IServerStateService, PlaceholderServerStateService>();
            builder.Services.AddSingleton(Moq.Mock.Of<ITimeSyncService>());
            builder.Services.AddSingleton(new EmergencyStopService());
            builder.Services.AddSingleton(Moq.Mock.Of<ILogService>());
            builder.Services.AddProblemDetails();
            builder.Services.ConfigureHttpJsonOptions(opts => {
                opts.SerializerOptions.TypeInfoResolverChain.Insert(0, AraJsonSerializerContext.Default);
                opts.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            });
            await using var app = builder.Build();
            app.MapServerStateEndpoints();
            await app.StartAsync();
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

            var resp = await http.GetAsync(new Uri("/api/v1/server/usage-history", UriKind.Relative));
            Assert.That(resp.Headers.CacheControl?.NoStore, Is.True);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.That(doc.RootElement.GetProperty("interval_seconds").GetInt32(), Is.EqualTo(15));
            Assert.That(doc.RootElement.GetProperty("samples").GetArrayLength(), Is.EqualTo(2));
            Assert.That(doc.RootElement.GetProperty("samples")[1].GetProperty("storage_free_bytes").GetInt64(), Is.EqualTo(90));

            var since = Uri.EscapeDataString(T0.ToString("o"));
            var newer = await http.GetStringAsync(new Uri($"/api/v1/server/usage-history?since={since}", UriKind.Relative));
            using var doc2 = JsonDocument.Parse(newer);
            Assert.That(doc2.RootElement.GetProperty("samples").GetArrayLength(), Is.EqualTo(1));
            await app.StopAsync();
        }
    }
}
