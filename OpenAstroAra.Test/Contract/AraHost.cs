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
using OpenAstroAra.Server;
using System;
using System.IO;
using System.Linq;

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

        protected override void ConfigureWebHost(IWebHostBuilder builder) {
            ArgumentNullException.ThrowIfNull(builder);
            builder.UseEnvironment("Production");
        }

        protected override void Dispose(bool disposing) {
            try {
                base.Dispose(disposing);
            } catch (AggregateException e) when (e.InnerExceptions.All(x => x is ObjectDisposedException)) {
                // Two parties stop this host: the factory here, and Program.Main's app.Run(), which
                // returns and disposes the app (and the services' cancellation sources) on its own
                // thread. When Main wins, the hosted services' StopAsync finds its source disposed.
                // The host is down either way; production stops it once, so this is harness-only.
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
    }
}