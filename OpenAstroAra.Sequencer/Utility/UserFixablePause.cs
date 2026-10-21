#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Sequencer.SequenceItem;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.Utility {

    /// <summary>
    /// Captures that can't go on until the user fixes the rig — the store disk is gone, or the
    /// camera is — park the run awaiting the user instead of failing every remaining frame, and
    /// Resume tries again. Shared by the steps that capture (Take Exposure, the flat sets).
    /// </summary>
    public static class UserFixablePause {

        /// <summary>True for the capture failures a person has to fix.</summary>
        public static bool Applies(Exception ex) => ex is StoreUnavailableException or CameraUnavailableException;

        /// <summary>What the user is asked to do, after the failure's own message.</summary>
        public static string Prompt(Exception ex) => ex switch {
            StoreUnavailableException => $"{ex.Message} Plug the drive back in, then press Resume",
            _ => $"{ex.Message} Check its USB cable and power, reconnect it if it doesn't come back by itself, then press Resume",
        };

        /// <summary>
        /// Run <paramref name="capture"/>; on a failure the user has to fix, pause the run awaiting
        /// them and run it again after Resume. Outside a run (no pause gate: validation, standalone
        /// execution) the failure propagates as before.
        /// </summary>
        public static async Task<T> RetryAfterUserFixAsync<T>(ISequenceItem item, string step, Func<Task<T>> capture,
                IProgress<ApplicationStatus>? progress, CancellationToken token) {
            var gate = (ItemUtility.GetRootContainer(item.Parent) as IPauseGateHost)?.PauseGate;
            while (true) {
                try {
                    return await capture();
                } catch (InvalidOperationException ex) when (gate is not null && Applies(ex)) {
                    await WaitAsync(gate, step, ex, progress, token);
                }
            }
        }

        /// <summary>Pause the run awaiting the user and wait for Resume.</summary>
        private static async Task WaitAsync(PauseGate gate, string step, Exception ex, IProgress<ApplicationStatus>? progress, CancellationToken token) {
            Logger.Warning($"{step}: {ex.Message} — pausing the run awaiting the user");
            progress?.Report(new ApplicationStatus() { Status = Prompt(ex) });
            gate.RequestPause(PauseKind.AwaitingUser);
            await gate.WaitWhilePausedAsync(token);
            Logger.Info($"{step}: resumed — trying again");
        }
    }
}
