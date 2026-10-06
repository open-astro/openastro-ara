#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NUnit.Framework;
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// Base for the live <c>*ConnectIntegrationTest</c> fixtures: probes the ASCOM OmniSim
    /// management API on <c>:32323</c> once per fixture and <see cref="Assert.Ignore(string)"/>s
    /// when none answers, so the fixtures skip cleanly outside the <c>alpaca-sim-integration</c>
    /// CI job. A fixture that needs scratch disk calls <see cref="CreateTempDir"/>; the base
    /// tears it down safely — <see cref="Assert.Ignore(string)"/> fires BEFORE the directory
    /// exists, and <c>Directory.Delete("")</c> in a teardown threw <c>ArgumentException</c>,
    /// failing a local run with every test green (#1198, from the #1211 review).
    /// </summary>
    public abstract class OmniSimIntegrationFixture {

        /// <summary>Discovery attempts the derived fixtures allow before failing.</summary>
        protected const int MaxDiscoveryAttempts = 6;

        private static readonly Uri ManagementProbeUri = new("http://127.0.0.1:32323/management/apiversions");

        /// <summary>Device name for the skip message ("skipping live Camera test").</summary>
        protected abstract string DeviceName { get; }

        /// <summary>The scratch directory from <see cref="CreateTempDir"/>, or "" when none was made.</summary>
        protected string TempDir { get; private set; } = string.Empty;

        [OneTimeSetUp]
        public async Task ProbeOmniSim() {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            try {
                using var resp = await http.GetAsync(ManagementProbeUri).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) {
                    Assert.Ignore($"OmniSim management API returned {(int)resp.StatusCode} on :32323 — skipping live {DeviceName} test.");
                }
            } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) {
                Assert.Ignore("No ASCOM OmniSim answering on :32323 — start one (or run the alpaca-sim-integration CI job) to exercise this test.");
            }
            await OnOmniSimAvailable().ConfigureAwait(false);
        }

        /// <summary>Fixture-specific setup that runs only once a simulator answered.</summary>
        protected virtual Task OnOmniSimAvailable() => Task.CompletedTask;

        /// <summary>Create a per-fixture scratch directory that <see cref="RemoveTempDir"/> deletes.</summary>
        protected string CreateTempDir(string prefix) {
            TempDir = Path.Combine(Path.GetTempPath(), $"oara-{prefix}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(TempDir);
            return TempDir;
        }

        [OneTimeTearDown]
        public void RemoveTempDir() {
            if (TempDir.Length == 0) {
                return;
            }
            try { Directory.Delete(TempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
