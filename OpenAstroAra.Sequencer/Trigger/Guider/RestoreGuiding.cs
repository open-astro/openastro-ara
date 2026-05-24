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
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.SequenceItem;
using OpenAstroAra.Sequencer.SequenceItem.Guider;
using OpenAstroAra.Sequencer.Validations;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using OpenAstroAra.Core.Locale;
using NINA.WPF.Base.Interfaces.ViewModel;
using OpenAstroAra.Core.Utility.Notification;
using OpenAstroAra.Sequencer.SequenceItem.Imaging;
using OpenAstroAra.Sequencer.Interfaces;

namespace OpenAstroAra.Sequencer.Trigger.Guider {

    [ExportMetadata("Name", "Lbl_SequenceTrigger_Guider_RestoreGuidings_Name")]
    [ExportMetadata("Description", "Lbl_SequenceTrigger_Guider_RestoreGuiding_Description")]
    [ExportMetadata("Icon", "GuiderSVG")]
    [ExportMetadata("Category", "Lbl_SequenceCategory_Guider")]
    [Export(typeof(ISequenceTrigger))]
    [JsonObject(MemberSerialization.OptIn)]
    public class RestoreGuiding : SequenceTrigger, IValidatable {
        private readonly IGuiderMediator guiderMediator;

        [ImportingConstructor]
        public RestoreGuiding(IGuiderMediator guiderMediator) : base() {
            this.guiderMediator = guiderMediator;
            TriggerRunner.Add(new StartGuiding(guiderMediator) { ForceCalibration = false });
        }

        private RestoreGuiding(RestoreGuiding cloneMe) : this(cloneMe.guiderMediator) {
            CopyMetaData(cloneMe);
        }

        public override object Clone() {
            return new RestoreGuiding(this) {
                TriggerRunner = (SequentialContainer)TriggerRunner.Clone()
            };
        }

        private IList<string> issues = new List<string>();

        public IList<string> Issues {
            get => issues;
            set {
                issues = ImmutableList.CreateRange(value);
                RaisePropertyChanged();
            }
        }

        public override async Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) {
            await TriggerRunner.Run(progress, token);
        }

        public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) {
            if (nextItem is IExposureItem) {
                var takeExposure = (IExposureItem)nextItem;
                return takeExposure.ImageType == "LIGHT";
            }
            return false;
        }

        public override string ToString() {
            return $"Trigger: {nameof(RestoreGuiding)}";
        }

        public bool Validate() {
            var i = new List<string>();
            var info = guiderMediator.GetInfo();

            if (!info.Connected) {
                i.Add(Loc.Instance["LblGuiderNotConnected"]);
            }

            Issues = i;
            return i.Count == 0;
        }
    }
}