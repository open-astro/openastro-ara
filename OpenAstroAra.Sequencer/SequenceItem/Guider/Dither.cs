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
using System.ComponentModel.Composition;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.SequenceItem.Guider {

    [ExportMetadata("Name", "Lbl_SequenceItem_Guider_Dither_Name")]
    [ExportMetadata("Description", "Lbl_SequenceItem_Guider_Dither_Description")]
    [ExportMetadata("Icon", "DitherSVG")]
    [ExportMetadata("Category", "Lbl_SequenceCategory_Guider")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class Dither : SequenceItem, IValidatable, IEstimatesOwnDuration {
        private readonly IGuiderMediator guiderMediator;
        private readonly IProfileService profileService;

        [ImportingConstructor]
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
            var gate = (ItemUtility.GetRootContainer(this.Parent) as IPauseGateHost)?.PauseGate;
            var settleRetried = false;
            while (true) {
                var sent = await guiderMediator.Dither(token);
                token.ThrowIfCancellationRequested();
                var error = sent ? guiderMediator.LastDitherError : "the guider is not guiding";
                if (error is null) {
                    return;
                }
                if (sent && !settleRetried) {
                    settleRetried = true;
                    Logger.Warning($"Dither: the guider did not settle ({error}) — dithering once more before pausing");
                    progress?.Report(new ApplicationStatus { Status = $"Guider did not settle ({error}) — trying once more" });
                    continue;
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