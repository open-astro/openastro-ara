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
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using OpenAstroAra.Server;
using OpenAstroAra.Server.Services;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1644 — the daemon's REAL composition root, started on a loopback port: a
    /// <c>PUT /api/v1/profile/site</c> reaches the broadcaster as <c>profile.updated</c> (the group's
    /// <c>AddEndpointFilter</c> in ProfileEndpoints), and a <c>PATCH /api/v1/client-settings</c> as
    /// <c>client_settings.updated</c> (the broadcaster Program.cs hands to <c>ClientSettingsService</c>).
    /// The unit tests call <c>PublishUpdatedAfterWrite</c> and build the service with their own fake,
    /// so they stay green if either wiring is deleted.</summary>
    [TestFixture]
    [Category("IO")]
    // Same isolation as CompositionRootSmokeTest (process-wide profile dir, port and Serilog logger).
    [NonParallelizable]
    public class PreferenceSyncWiringTest {

        private string profileDir = null!;
        private string? previousProfileDir;
        private string? previousPort;
        private WebApplication app = null!;
        private HttpClient http = null!;

        [OneTimeSetUp]
        public async Task StartTheDaemon() {
            profileDir = Path.Combine(Path.GetTempPath(), $"oara-prefsync-{Guid.NewGuid():N}");
            Directory.CreateDirectory(profileDir);
            previousProfileDir = Environment.GetEnvironmentVariable("OPENASTROARA_PROFILE_DIR");
            previousPort = Environment.GetEnvironmentVariable("OPENASTROARA_PORT");
            Environment.SetEnvironmentVariable("OPENASTROARA_PROFILE_DIR", profileDir);
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Environment.SetEnvironmentVariable("OPENASTROARA_PORT", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            app = Program.BuildApp([]);
            await app.StartAsync();
            http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        }

        [OneTimeTearDown]
        public async Task StopTheDaemon() {
            http?.Dispose();
            OpenAstroAra.Core.Utility.Notification.Notifier.Use(null);
            await app.StopAsync();
            await app.DisposeAsync();
            await Serilog.Log.CloseAndFlushAsync();
            Environment.SetEnvironmentVariable("OPENASTROARA_PROFILE_DIR", previousProfileDir);
            Environment.SetEnvironmentVariable("OPENASTROARA_PORT", previousPort);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try {
                Directory.Delete(profileDir, recursive: true);
            } catch (IOException) {
            } catch (UnauthorizedAccessException) {
            }
        }

        [Test]
        public async Task A_site_save_over_HTTP_reaches_the_broadcaster_as_profile_updated() {
            var seen = await Collect(() => http.PutAsync(new Uri("/api/v1/profile/site", UriKind.Relative),
                new StringContent("{}", Encoding.UTF8, "application/json")), "profile.updated");
            Assert.That(seen, Does.Contain("\"section\":\"site\""));
        }

        [Test]
        public async Task A_client_settings_patch_over_HTTP_reaches_the_broadcaster_as_client_settings_updated() {
            var seen = await Collect(() => http.PatchAsync(new Uri("/api/v1/client-settings", UriKind.Relative),
                new StringContent("{\"settings\":{\"k\":1}}", Encoding.UTF8, "application/json")), "client_settings.updated");
            Assert.That(seen, Does.Contain("updated_utc"));
        }

        /// <summary>Subscribes first, sends the request, and returns the payload of the first event of
        /// <paramref name="type"/> (empty when none arrives within 10 s).</summary>
        private async Task<string> Collect(Func<Task<HttpResponseMessage>> request, string type) {
            var ws = app.Services.GetRequiredService<IWsEventChannel>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var found = new TaskCompletionSource<string>();
            var reader = Task.Run(async () => {
                try {
                    await foreach (var e in ws.ReadAllAsync(cts.Token)) {
                        if (e.Type == type) {
                            found.TrySetResult(e.Payload.GetRawText());
                            return;
                        }
                    }
                } catch (OperationCanceledException) {
                }
                found.TrySetResult("");
            });
            using var response = await request();
            Assert.That(response.IsSuccessStatusCode, Is.True, await response.Content.ReadAsStringAsync());
            var payload = await found.Task;
            await cts.CancelAsync();
            await reader;
            Assert.That(payload, Is.Not.Empty, $"no {type} event reached the broadcaster");
            return payload;
        }
    }
}