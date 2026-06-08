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
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Sequencer.Validations;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.SequenceItem.Telescope {

    [ExportMetadata("Name", "Lbl_SequenceItem_Telescope_SetTracking_Name")]
    [ExportMetadata("Description", "Lbl_SequenceItem_Telescope_SetTracking_Description")]
    [ExportMetadata("Icon", "SpeedometerSVG")]
    [ExportMetadata("Category", "Lbl_SequenceCategory_Telescope")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class SetTracking : SequenceItem, IValidatable {
        private static readonly IList<TrackingMode> trackingModeChoices = ImmutableList.Create(
            TrackingMode.Sidereal,
            TrackingMode.King,
            TrackingMode.Solar,
            TrackingMode.Lunar,
            TrackingMode.Stopped);

        [ImportingConstructor]
        public SetTracking(ITelescopeMediator telescopeMediator) {
            this.telescopeMediator = telescopeMediator;
        }

        private SetTracking(SetTracking cloneMe) : this(cloneMe.telescopeMediator) {
            CopyMetaData(cloneMe);
        }

        public override object Clone() {
            return new SetTracking(this) {
                TrackingMode = TrackingMode
            };
        }

        private ITelescopeMediator telescopeMediator;
        private IList<string> issues = new List<string>();

        public IList<string> Issues {
            get => issues;
            set {
                issues = value;
                RaisePropertyChanged();
            }
        }

        private TrackingMode trackingMode = TrackingMode.Sidereal;

        [JsonProperty]
        public TrackingMode TrackingMode {
            get => trackingMode;
            set {
                trackingMode = value;
                RaisePropertyChanged();
            }
        }

        public static IList<TrackingMode> TrackingModeChoices => trackingModeChoices;

        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            telescopeMediator.SetTrackingMode(TrackingMode);
            return Task.CompletedTask;
        }

        public bool Validate() {
            var i = new List<string>();
            if (!telescopeMediator.GetInfo().Connected) {
                i.Add(Loc.Instance["LblTelescopeNotConnected"]);
            } else if (!(telescopeMediator.GetInfo().TrackingModes?.Contains(TrackingMode) == true)) {
                i.Add(Loc.Instance["LblTrackingModeNotSupported"]);
            }
            Issues = i;
            return i.Count == 0;
        }

        public override void AfterParentChanged() {
            Validate();
        }

        public override string ToString() {
            return $"Category: {Category}, Item: {nameof(SetTracking)}, {nameof(TrackingMode)}: {TrackingMode}";
        }
    }
}