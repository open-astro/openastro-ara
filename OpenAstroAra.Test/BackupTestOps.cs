#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §43-2 async create moved packaging onto a background worker, so "create a backup" in a test is now
    /// POST + poll-create-status-to-terminal. This helper keeps the many pre-worker call sites one-line:
    /// it throws on the failed terminal (matching the old synchronous throw-on-failure contract) and
    /// times out rather than hanging a broken state machine.
    /// </summary>
    internal static class BackupTestOps {

        public static async Task<OperationAcceptedDto> CreateAndAwaitAsync(IBackupService svc, string? idempotencyKey = null) {
            var op = await svc.CreateZipAsync(idempotencyKey, CancellationToken.None).ConfigureAwait(false);
            var state = await AwaitCreateTerminalAsync(svc).ConfigureAwait(false);
            if (state.Item1 != "done") {
                throw new InvalidOperationException("backup create failed: " + state.Item2);
            }
            return op;
        }

        /// <summary>Polls create-status to its terminal, returning (state, message). For tests asserting the
        /// FAILED terminal directly.</summary>
        public static async Task<(string State, string? Message)> AwaitCreateTerminalAsync(IBackupService svc) {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline) {
                var status = await svc.GetCreateStatusAsync(CancellationToken.None).ConfigureAwait(false);
                var state = status.GetProperty("state").GetString();
                if (state is "done" or "failed") {
                    string? message = status.TryGetProperty("message", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.String
                        ? m.GetString() : null;
                    return (state!, message);
                }
                await Task.Delay(25).ConfigureAwait(false);
            }
            throw new TimeoutException("backup create did not reach a terminal state within 15s");
        }
    }
}
