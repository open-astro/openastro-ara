#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace OpenAstroAra.Test.Contract {

    /// <summary>#1548 — a real AlpacaBridge process the test starts, kills and starts again (the CI job installs
    /// the pinned release .deb and sets <see cref="BinVar"/> and <see cref="PortVar"/>). No simulator devices:
    /// the bridge itself is the thing Ara talks to.</summary>
    internal sealed class BridgeProcess : IDisposable {
        internal const string BinVar = "ARA_CONTRACT_BRIDGE_BIN";
        internal const string PortVar = "ARA_CONTRACT_BRIDGE_PORT";
        internal const string Host = "127.0.0.1";

        private readonly string bin;
        private readonly string workDir = Path.Combine(Path.GetTempPath(), $"oara-bridge-{Guid.NewGuid():N}");
        private Process? process;

        private BridgeProcess(string bin, int port) {
            this.bin = bin;
            Port = port;
            Directory.CreateDirectory(workDir);
        }

        public int Port { get; }

        /// <summary>Null when the environment names no bridge: the contract tests then skip.</summary>
        public static BridgeProcess? FromEnvironment() {
            var bin = Environment.GetEnvironmentVariable(BinVar);
            var port = Environment.GetEnvironmentVariable(PortVar);
            if (string.IsNullOrWhiteSpace(bin) || !int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var p)) {
                return null;
            }
            return new BridgeProcess(bin, p);
        }

        public async Task StartAsync() {
            // The state file (registered_devices.json) is resolved against the working directory.
            var info = new ProcessStartInfo(bin) { WorkingDirectory = workDir, UseShellExecute = false };
            info.Environment["ALPACAHTTP_PORT"] = Port.ToString(CultureInfo.InvariantCulture);
            info.Environment["ALPACAHTTP_DISCOVERY"] = "false";
            info.Environment["ALPACAHTTP_FILE_LOGGING"] = "false";
            process = Process.Start(info) ?? throw new InvalidOperationException($"could not start {bin}");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < deadline) {
                if (process.HasExited) {
                    throw new InvalidOperationException($"{bin} exited with {process.ExitCode} before it answered");
                }
                try {
                    using var r = await http.GetAsync(new Uri($"http://{Host}:{Port}/management/apiversions")).ConfigureAwait(false);
                    if (r.IsSuccessStatusCode) {
                        return;
                    }
                } catch (HttpRequestException) {
                    // not listening yet
                } catch (TaskCanceledException) {
                    // accepted but not answering yet
                }
                await Task.Delay(250).ConfigureAwait(false);
            }
            throw new TimeoutException($"{bin} did not answer on port {Port} within 30 s");
        }

        /// <summary>SIGKILL: the sockets drop with no goodbye, as on a crash or a power cut.</summary>
        public async Task KillAsync() {
            if (process is { HasExited: false }) {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            process?.Dispose();
            process = null;
        }

        public void Dispose() {
            KillAsync().GetAwaiter().GetResult();
            try {
                Directory.Delete(workDir, recursive: true);
            } catch (IOException) {
                // best effort
            }
        }
    }
}