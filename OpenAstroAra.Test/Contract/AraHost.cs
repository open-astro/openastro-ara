#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenAstroAra.Server;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test.Contract {

    /// <summary>#1548 — the real Ara Server host (<c>Program</c>'s own composition root, not a rebuilt
    /// subset) in-process on a TestServer, with a throw-away profile directory. A test drives it only
    /// through <see cref="System.Net.Http.HttpClient"/> calls on its public routes.</summary>
    internal sealed class AraHost : WebApplicationFactory<Program> {
        private readonly string profileDir = Path.Combine(Path.GetTempPath(), $"oara-contract-{Guid.NewGuid():N}");
        private readonly string? previousProfileDir = Environment.GetEnvironmentVariable("OPENASTROARA_PROFILE_DIR");

        public AraHost() {
            Directory.CreateDirectory(profileDir);
            // Read by BuildApp at startup, process-wide: restored in Dispose.
            Environment.SetEnvironmentVariable("OPENASTROARA_PROFILE_DIR", profileDir);
        }

        private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);

        private readonly TaskCompletionSource hostDisposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override void ConfigureWebHost(IWebHostBuilder builder) {
            ArgumentNullException.ThrowIfNull(builder);
            builder.UseEnvironment("Production");
            // Built by the container, so the container disposes it: the last step of the host's own dispose.
            builder.ConfigureServices(s => s.AddSingleton(_ => new DisposeSignal(hostDisposed)));
            // Never ask systemd or polkit for anything (a guider start pops a password dialog on a desktop).
            builder.ConfigureTestServices(TestHostSeams.Replace);
        }

        // Program.Main runs app.Run() on its own thread, and app.Run() stops and disposes the host once
        // ApplicationStopping fires. The factory's dispose would run a second Host.StopAsync at the same
        // time, and the hosted services' StopAsync is not safe against a concurrent second stop (disposed
        // cancellation sources, a half-cleaned mDNS advertiser). So Main is the only party that stops the
        // host, as in production: the factory's stop asks for the stop and waits for Main's dispose.
        protected override IHost CreateHost(IHostBuilder builder) {
            var host = base.CreateHost(builder);
            host.Services.GetRequiredService<DisposeSignal>(); // created now, disposed with the host
            return new StoppedByMainHost(host, hostDisposed.Task);
        }

        protected override void Dispose(bool disposing) {
            try {
                base.Dispose(disposing);
            } finally {
                if (disposing) {
                    Environment.SetEnvironmentVariable("OPENASTROARA_PROFILE_DIR", previousProfileDir);
                    try {
                        Directory.Delete(profileDir, recursive: true);
                    } catch (IOException) {
                        // best effort: the temp dir is not worth failing a teardown for
                    }
                }
            }
        }

        private sealed class DisposeSignal(TaskCompletionSource disposed) : IDisposable {
            public void Dispose() => disposed.TrySetResult();
        }

        private sealed class StoppedByMainHost(IHost inner, Task disposed) : IHost {
            private readonly IHostApplicationLifetime lifetime = inner.Services.GetRequiredService<IHostApplicationLifetime>();

            public IServiceProvider Services => inner.Services;

            public Task StartAsync(CancellationToken cancellationToken = default) => inner.StartAsync(cancellationToken);

            public Task StopAsync(CancellationToken cancellationToken = default) {
                lifetime.StopApplication();
                return disposed.WaitAsync(StopTimeout, cancellationToken);
            }

            public void Dispose() {
                // Program.Main's app.Run() disposed the host.
            }
        }
    }
}