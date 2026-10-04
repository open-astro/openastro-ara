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
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Core.Utility;
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

    [ExportMetadata("Name", "Lbl_SequenceItem_Guider_StartGuiding_Name")]
    [ExportMetadata("Description", "Lbl_SequenceItem_Guider_StartGuiding_Description")]
    [ExportMetadata("Icon", "GuiderSVG")]
    [ExportMetadata("Category", "Lbl_SequenceCategory_Guider")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class StartGuiding : SequenceItem, IValidatable {
        private IGuiderMediator guiderMediator;

        [ImportingConstructor]
        public StartGuiding(IGuiderMediator guiderMediator) {
            this.guiderMediator = guiderMediator;
        }

        private StartGuiding(StartGuiding cloneMe) : this(cloneMe.guiderMediator) {
            CopyMetaData(cloneMe);
        }

        public override object Clone() {
            return new StartGuiding(this) {
                ForceCalibration = ForceCalibration
            };
        }

        [JsonProperty]
        public bool ForceCalibration { get; set; } = false;

        private IList<string> issues = new List<string>();

        public IList<string> Issues {
            get => issues;
            set {
                issues = value;
                RaisePropertyChanged();
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "A guider fault of any kind is a reason to park the run awaiting the user, with its message, not a reason to image unguided; cancellation is rethrown.")]
        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            // A rig with a guider does not image unguided by accident: when guiding will not start the
            // run parks awaiting the user (the guider refused, lost its star, is not connected…) and
            // Resume tries again. Only a run with no pause gate (validation, standalone execution)
            // still fails the instruction outright. Night of 2026-10-03: Start Guiding failed once,
            // the run carried on unguided for an hour and every dither was skipped.
            var gate = (ItemUtility.GetRootContainer(this.Parent) as IPauseGateHost)?.PauseGate;
            var attempt = 0;
            while (true) {
                attempt++;
                string? reason = null;
                bool started;
                try {
                    started = await guiderMediator.StartGuiding(ForceCalibration, progress, token);
                } catch (OperationCanceledException) {
                    throw;
                } catch (Exception ex) {
                    started = false;
                    reason = ex.Message;
                }
                if (started) {
                    if (attempt > 1) {
                        Logger.Info($"Start guiding: guiding started on attempt {attempt}");
                    }
                    return;
                }
                var why = string.IsNullOrWhiteSpace(reason) ? "the guider did not start guiding" : reason;
                if (gate is null) {
                    throw new SequenceEntityFailedException($"Failed to start guiding: {why}");
                }
                Logger.Warning($"Start guiding: {why} — pausing the run awaiting the user (attempt {attempt})");
                progress.Report(new ApplicationStatus() {
                    Status = $"Guiding did not start ({why}) — check the guider, then press Resume to try again"
                });
                gate.RequestPause(PauseKind.AwaitingUser);
                await gate.WaitWhilePausedAsync(token);
                Logger.Info("Start guiding: resumed — trying to start guiding again");
            }
        }

        public bool Validate() {
            bool validated = true;
            var i = new List<string>();
            if (!guiderMediator.GetInfo().Connected) {
                i.Add(Loc.Instance["LblGuiderNotConnected"]);
                validated = false;
            }
            if (ForceCalibration && !guiderMediator.GetInfo().CanClearCalibration) {
                i.Add(Loc.Instance["LblGuiderCannotClearCalibration"]);
            }
            Issues = i;

            return validated;
        }

        public override void AfterParentChanged() {
            Validate();
        }

        public override string ToString() {
            return $"Category: {Category}, Item: {nameof(StartGuiding)}";
        }
    }
}