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
using OpenAstroAra.Core.Locale;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Sequencer.Utility;
using OpenAstroAra.Sequencer.Validations;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.SequenceItem.Guider {

    [JsonObject(MemberSerialization.OptIn)]
    public class Dither : SequenceItem, IValidatable, IEstimatesOwnDuration {
        private readonly IGuiderMediator guiderMediator;
        private readonly IProfileService profileService;

        public Dither(IGuiderMediator guiderMediator, IProfileService profileService) {
            this.guiderMediator = guiderMediator;
            this.profileService = profileService;
        }

        private Dither(Dither cloneMe) : this(cloneMe.guiderMediator, cloneMe.profileService) {
            CopyMetaData(cloneMe);
        }

        public override object Clone() {
            return new Dither(this);
        }

        private IList<string> issues = new List<string>();

        public IList<string> Issues {
            get => issues;
            set {
                issues = value;
                RaisePropertyChanged();
            }
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            // A frame is never started on a star that is still swinging. The night of 2026-10-16: two
            // dithers failed to settle (a rotated calibration spiralled every correction) and the next
            // exposure started anyway — both frames trailed. A settle failure gets ONE more dither (the
            // guider settles again from wherever it is); a second failure, or a dither the guider refused
            // because it is not guiding, parks the run awaiting the user, and Resume dithers again. A rig
            // whose guider is not connected keeps the old behaviour: nothing to wait for.
            if (!guiderMediator.GetInfo().Connected) {
                await guiderMediator.Dither(token);
                return;
            }
            var gate = ItemUtility.GetPauseGate(this.Parent);
            var settleRetried = false;
            var restartTried = false;
            while (true) {
                var sent = await guiderMediator.Dither(token);
                token.ThrowIfCancellationRequested();
                var verdict = guiderMediator.LastDitherError;
                var notGuiding = !sent && (verdict is null or "refused") && guiderMediator.GetInfo().Connected;
                var error = sent ? verdict
                    : verdict is not (null or "refused") ? verdict
                    : guiderMediator.GetInfo().Connected ? "the guider is not guiding" : "the guider is not connected";
                if (error is null) {
                    return;
                }
                if (sent && !settleRetried) {
                    settleRetried = true;
                    Logger.Warning($"Dither: the guider did not settle ({error}) — dithering once more before pausing");
                    progress?.Report(new ApplicationStatus { Status = $"Guider did not settle ({error}) — trying once more" });
                    continue;
                }
                if (notGuiding && !restartTried) {
                    // A guider that is merely not guiding (it stopped on a slew, or lost its star for longer
                    // than the dither waits) is restarted here, the way an ASIAIR carries on: the guider
                    // picks a star, guides on its stored calibration and settles, then the dither goes
                    // ahead. The user is only asked when that restart fails. Rig night 2026-10-18: a
                    // 2-frame star loss and a Planning GoTo each parked the run, and every Resume parked
                    // it again within 2 ms because guiding had to be restarted by hand first.
                    restartTried = true;
                    Logger.Warning("Dither: the guider is not guiding — restarting guiding before the dither");
                    progress?.Report(new ApplicationStatus { Status = "Guider is not guiding — restarting guiding, then dithering" });
                    if (await TryRestartGuidingAsync(progress, token)) {
                        continue;
                    }
                    error = "the guider is not guiding and restarting it did not help";
                }
                var why = sent ? $"the guider did not settle twice ({error})" : error;
                if (gate is null) {
                    throw new SequenceEntityFailedException($"Dither failed: {why}");
                }
                Logger.Warning($"Dither: {why} — pausing the run awaiting the user");
                progress?.Report(new ApplicationStatus {
                    Status = $"Dither failed ({why}) — check the guider, then press Resume to dither again"
                });
                gate.RequestPause(PauseKind.AwaitingUser);
                await gate.WaitWhilePausedAsync(token);
                Logger.Info("Dither: resumed — dithering again");
                settleRetried = false;
                restartTried = false; // Resume restarts guiding itself if the guider is still stopped
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "A guider fault while restarting is the reason to park the run awaiting the user, with its message; cancellation is rethrown.")]
        private async Task<bool> TryRestartGuidingAsync(IProgress<ApplicationStatus>? progress, CancellationToken token) {
            try {
                return await guiderMediator.StartGuiding(false, progress ?? new Progress<ApplicationStatus>(), token);
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                Logger.Warning($"Dither: restarting guiding failed — {ex.Message}");
                return false;
            }
        }

        public bool Validate() {
            var i = new List<string>();
            if (!guiderMediator.GetInfo().Connected) {
                i.Add(Loc.Instance["LblGuiderNotConnected"]);
            }
            Issues = i;
            return i.Count == 0;
        }

        public override void AfterParentChanged() {
            Validate();
        }

        public override string ToString() {
            return $"Category: {Category}, Item: {nameof(Dither)}";
        }

        public override TimeSpan GetEstimatedDuration() {
            return TimeSpan.FromSeconds(Math.Min(profileService.ActiveProfile.GuiderSettings.SettleTimeout, 300));
        }
    }
}