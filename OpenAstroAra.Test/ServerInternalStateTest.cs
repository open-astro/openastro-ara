#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using OpenAstroAra.Server;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>§66.7 / #1175 — the live internal-state sample and its route.</summary>
    [TestFixture]
    public class ServerInternalStateTest {

        [Test]
        public void A_sample_carries_the_runtime_figures() {
            var t0 = new DateTimeOffset(2026, 10, 13, 20, 0, 0, TimeSpan.Zero);
            var now = t0;
            var svc = new ServerInternalStateService(() => now, startedUtc: t0);
            now = t0.AddSeconds(90);

            var s = svc.Sample();

            Assert.That(s.CapturedUtc, Is.EqualTo(now));
            Assert.That(s.UptimeSeconds, Is.EqualTo(90).Within(0.001));
            Assert.That(s.Process.WorkingSetBytes, Is.GreaterThan(0));
            Assert.That(s.Process.ManagedHeapBytes, Is.GreaterThan(0));
            Assert.That(s.Process.ThreadCount, Is.GreaterThan(0));
            Assert.That(s.Process.CpuPercent, Is.Null, "the first sample has nothing to diff against");
            Assert.That(s.WebSockets.MaxConnections, Is.EqualTo(WsConnectionGate.MaxConcurrentConnections));
            Assert.That(s.Host.ProcessorCount, Is.EqualTo(Environment.ProcessorCount));
            Assert.That(s.Host.Architecture, Is.Not.Empty);

            now = now.AddSeconds(1);
            Assert.That(svc.Sample().Process.CpuPercent, Is.Not.Null.And.GreaterThanOrEqualTo(0), "the second sample has a CPU share");
        }

        [Test]
        public async Task Uptime_counts_from_the_process_start_not_from_the_first_read() {
            // r2 finding: the singleton is built on the first panel read, possibly days after boot.
            var processStart = new DateTimeOffset(Process.GetCurrentProcess().StartTime.ToUniversalTime());
            await Task.Delay(300);
            var builtLate = new ServerInternalStateService(); // "the first GET", well after the process started

            var uptime = builtLate.Sample().UptimeSeconds;

            var expected = (DateTimeOffset.UtcNow - processStart).TotalSeconds;
            Assert.That(uptime, Is.GreaterThan(0.3), "construction time is not the daemon's start");
            Assert.That(uptime, Is.EqualTo(expected).Within(5), "it is the process's age");
        }

        [Test]
        public void Cpu_percent_is_the_process_share_of_one_core_between_samples() {
            var t0 = new DateTimeOffset(2026, 10, 13, 20, 0, 0, TimeSpan.Zero);
            var svc = new ServerInternalStateService(() => t0);
            Assert.That(svc.CpuPercent(TimeSpan.FromMilliseconds(100), t0), Is.Null);
            // 500 ms of CPU over 1 s of wall = half a core.
            Assert.That(svc.CpuPercent(TimeSpan.FromMilliseconds(600), t0.AddSeconds(1)), Is.EqualTo(50).Within(0.001));
            // Two cores flat out for 2 s.
            Assert.That(svc.CpuPercent(TimeSpan.FromMilliseconds(4600), t0.AddSeconds(3)), Is.EqualTo(200).Within(0.001));
            Assert.That(svc.CpuPercent(TimeSpan.FromMilliseconds(4600), t0.AddSeconds(3)), Is.Null, "no wall time elapsed → no figure");
        }

        [Test]
        public void Load_average_reads_the_first_field_and_is_null_when_absent() {
            var path = Path.Combine(Path.GetTempPath(), "oara-loadavg-" + Guid.NewGuid().ToString("N"));
            try {
                File.WriteAllText(path, "0.42 0.37 0.30 1/412 12345\n");
                Assert.That(ServerInternalStateService.ReadLoadAverage(path), Is.EqualTo(0.42).Within(1e-9));
                File.WriteAllText(path, "garbage\n");
                Assert.That(ServerInternalStateService.ReadLoadAverage(path), Is.Null);
            } finally {
                File.Delete(path);
            }
            Assert.That(ServerInternalStateService.ReadLoadAverage(path + ".missing"), Is.Null);
        }
    }

    /// <summary>The route, over a real loopback host, plus the §66.10 latency budget as an
    /// opt-in gate: the figures are always recorded in the test output; they fail the run only
    /// with <c>OPENASTROARA_PERF_GATE=1</c> (a loaded CI runner must not turn an aspirational
    /// budget into a red build).</summary>
    [TestFixture]
    [Category("IO")]
    public class ServerInternalStateRouteTest {
        private WebApplication app = null!;
        private HttpClient http = null!;

        [OneTimeSetUp]
        public async Task StartKestrel() {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton<ServerInternalStateService>();
            builder.Services.AddSingleton<IServerStateService, PlaceholderServerStateService>();
            // The rest of the /api/v1/server group resolves these from DI; the group fails to build without them.
            builder.Services.AddSingleton(Moq.Mock.Of<ITimeSyncService>());
            builder.Services.AddSingleton(new EmergencyStopService());
            builder.Services.AddSingleton(Moq.Mock.Of<ILogService>());
            builder.Services.AddProblemDetails();
            builder.Services.ConfigureHttpJsonOptions(opts => {
                opts.SerializerOptions.TypeInfoResolverChain.Insert(0, AraJsonSerializerContext.Default);
                opts.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            });
            app = builder.Build();
            app.MapGet("/healthz", (HttpContext h) => Results.Text("ok", contentType: "text/plain"));
            app.MapServerStateEndpoints();
            await app.StartAsync();
            http = new HttpClient { BaseAddress = new Uri(app.Urls.First()), Timeout = TimeSpan.FromMinutes(1) };
        }

        [OneTimeTearDown]
        public async Task StopKestrel() {
            http.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }

        [Test]
        public void The_sample_serializes_through_the_aot_context() {
            var svc = app.Services.GetRequiredService<ServerInternalStateService>();
            string json = null!;
            Assert.DoesNotThrow(() => json = JsonSerializer.Serialize(svc.Sample(), AraJsonSerializerContext.Default.ServerInternalStateDto));
            Assert.That(json, Does.Contain("uptime_seconds").Or.Contain("UptimeSeconds"));
        }

        [Test]
        public async Task A_first_read_long_after_the_host_started_reports_the_hosts_age() {
            // Resolved through the host for the first time here, after the OneTimeSetUp built it.
            await Task.Delay(200);
            var resp = await http.GetAsync(new Uri("/api/v1/server/internal-state", UriKind.Relative));
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.That(doc.RootElement.GetProperty("uptime_seconds").GetDouble(), Is.GreaterThan(0.2),
                "uptime must predate this first read (process start, not singleton construction)");
        }

        [Test]
        public async Task The_route_returns_the_snake_cased_sample_with_no_store() {
            var resp = await http.GetAsync(new Uri("/api/v1/server/internal-state", UriKind.Relative));
            var body = await resp.Content.ReadAsStringAsync();
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK), body);
            Assert.That(resp.Headers.CacheControl?.NoStore, Is.True);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            Assert.That(root.GetProperty("uptime_seconds").GetDouble(), Is.GreaterThanOrEqualTo(0));
            Assert.That(root.GetProperty("process").GetProperty("working_set_bytes").GetInt64(), Is.GreaterThan(0));
            Assert.That(root.GetProperty("web_sockets").GetProperty("max_connections").GetInt32(), Is.EqualTo(WsConnectionGate.MaxConcurrentConnections));
            Assert.That(root.GetProperty("host").GetProperty("processor_count").GetInt32(), Is.GreaterThan(0));
        }

        [Test]
        public async Task Latency_budget_is_recorded_and_gates_only_on_request() {
            // §66.10: GET /healthz baseline < 10 ms, hard limit 100 ms; the snapshot route
            // baseline < 200 ms, hard limit 1 s. Warm each once, then time the median of five.
            var gate = Environment.GetEnvironmentVariable("OPENASTROARA_PERF_GATE") == "1";
            var healthz = await MedianMsAsync("/healthz");
            var state = await MedianMsAsync("/api/v1/server/internal-state");
            await TestContext.Out.WriteLineAsync($"§66.10 latency: GET /healthz {healthz:F1} ms (hard limit 100); GET /server/internal-state {state:F1} ms (hard limit 1000); gate={gate}");
            if (gate) {
                Assert.That(healthz, Is.LessThan(100), "GET /healthz hard limit (§66.10)");
                Assert.That(state, Is.LessThan(1000), "GET /server/internal-state hard limit (§66.10)");
            }
        }

        private async Task<double> MedianMsAsync(string path) {
            var uri = new Uri(path, UriKind.Relative);
            (await http.GetAsync(uri)).EnsureSuccessStatusCode();
            var samples = new double[5];
            for (var i = 0; i < samples.Length; i++) {
                var sw = Stopwatch.StartNew();
                (await http.GetAsync(uri)).EnsureSuccessStatusCode();
                samples[i] = sw.Elapsed.TotalMilliseconds;
            }
            Array.Sort(samples);
            return samples[2];
        }
    }
}
