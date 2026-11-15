#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Newtonsoft.Json;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Sequencer.Utility;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.SequenceItem.Autofocus {

    /// <summary>
    /// NINA's "Run Autofocus" — runs an autofocus routine on the focuser. Common in real plans
    /// (one per target, plus filter changes). No serialized fields beyond the base.
    ///
    /// Ported as a first-class type so NINA exports round-trip instead of degrading to an
    /// <see cref="UnknownSequenceItem"/>. EXECUTION runs the real §59.8 V-curve sweep through
    /// <see cref="IAutofocusExecutor"/> (probe positions → HFR → curve fit → move to best); a
    /// failed sweep is retried (3 attempts in all), then parks the run awaiting the user; an unwired
    /// sweep, or a failure with no pause gate, fails this step loudly rather than silently skipping
    /// focus (which would quietly ruin the subsequent frames).
    /// </summary>
    [JsonObject(MemberSerialization.OptIn)]
    public class RunAutofocus : SequenceItem {

        public RunAutofocus(IAutofocusExecutor? autofocusExecutor = null) {
            this.autofocusExecutor = autofocusExecutor;
        }

        private readonly IAutofocusExecutor? autofocusExecutor;

        /// <summary>A failed sweep is tried this many times in all before the run parks.</summary>
        internal const int MaxAttempts = 3;

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (autofocusExecutor is null) {
                throw new SequenceEntityFailedException("Autofocus is not wired for sequence execution on this daemon.");
            }
            // #1892: imaging on out of focus ruins every frame. Retry, then park awaiting the user (the Start
            // Guiding pattern; Resume tries again). Outside a run (no pause gate) the failure throws as before.
            var gate = ItemUtility.GetPauseGate(Parent);
            while (true) {
                AutofocusFailure? failure = null;
                for (var attempt = 1; attempt <= MaxAttempts; attempt++) {
                    if (await autofocusExecutor.RunAutofocusAsync(progress, token)) {
                        if (attempt > 1) {
                            Logger.Info($"Autofocus: succeeded on attempt {attempt}");
                        }
                        return;
                    }
                    token.ThrowIfCancellationRequested();
                    failure = autofocusExecutor.LastFailure;
                    if (failure is { Cancelled: true }) {
                        throw new SequenceEntityFailedException("Autofocus was cancelled.");
                    }
                    Logger.Warning($"Autofocus: attempt {attempt} of {MaxAttempts} failed: {failure?.Reason ?? "no reason reported"}");
                }
                var why = failure?.Reason ?? "see the daemon log (probe quality, curve fit, or focuser fault)";
                var hint = failure is { AtFocuserLimit: true }
                    ? " The best focus sat at the focuser's travel limit: re-zero the focuser or move it out, then press Resume."
                    : " Check the focuser and sky, then press Resume to try again.";
                if (gate is null) {
                    throw new SequenceEntityFailedException($"Autofocus failed after {MaxAttempts} attempts: {why}.{hint}");
                }
                Logger.Warning($"Autofocus: failed {MaxAttempts} times ({why}) - pausing the run awaiting the user");
                progress.Report(new ApplicationStatus() {
                    Status = $"Autofocus failed {MaxAttempts} times ({why}).{hint}"
                });
                gate.RequestPause(PauseKind.AwaitingUser);
                await gate.WaitWhilePausedAsync(token);
                Logger.Info("Autofocus: resumed - trying again");
            }
        }

        public override object Clone() {
            return new RunAutofocus(autofocusExecutor) {
                Icon = Icon,
                Name = Name,
                Category = Category,
                Description = Description,
            };
        }
    }
}
