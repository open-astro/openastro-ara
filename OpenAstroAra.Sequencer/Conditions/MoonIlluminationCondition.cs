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
using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Equipment.Equipment.MyGuider.SkyGuard.SkyGuardMessages;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Sequencer.SequenceItem;
using OpenAstroAra.Sequencer.SequenceItem.Utility;
using OpenAstroAra.Sequencer.Utility;
using System;
using System.ComponentModel.Composition;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.Conditions {

    [ExportMetadata("Name", "Lbl_SequenceCondition_MoonIlluminationCondition_Name")]
    [ExportMetadata("Description", "Lbl_SequenceCondition_MoonIlluminationCondition_Description")]
    [ExportMetadata("Icon", "BrightnessSVG")]
    [ExportMetadata("Category", "Lbl_SequenceCategory_Condition")]
    [Export(typeof(ISequenceCondition))]
    [JsonObject(MemberSerialization.OptIn)]
    public class MoonIlluminationCondition : SequenceCondition {
        private double userMoonIllumination;
        private double currentMoonIllumination;
        private ComparisonOperator comparator;

        [ImportingConstructor]
        public MoonIlluminationCondition() {
            UserMoonIllumination = 0d;
            Comparator = ComparisonOperator.GreaterThan;

            CalculateCurrentMoonState();
            ConditionWatchdog = new ConditionWatchdog(InterruptWhenMoonOutsideOfBounds, TimeSpan.FromSeconds(5));
        }

        private async Task InterruptWhenMoonOutsideOfBounds() {
            CalculateCurrentMoonState();
            if (!Check(null, null)) {
                if (this.Parent != null) {
                    if (ItemUtility.IsInRootContainer(Parent) && this.Parent.Status == SequenceEntityStatus.RUNNING && this.Status != SequenceEntityStatus.DISABLED) {
                        Logger.Info("Moon is outside of the specified illumination range - Interrupting current Instruction Set");
                        await this.Parent.Interrupt();
                    }
                }
            }
        }

        private MoonIlluminationCondition(MoonIlluminationCondition cloneMe) : this() {
            CopyMetaData(cloneMe);
        }

        public override object Clone() {
            return new MoonIlluminationCondition(this) {
                UserMoonIllumination = UserMoonIllumination,
                Comparator = Comparator
            };
        }

        [OnDeserialized]
        public void OnDeserialized(StreamingContext context) {
            RunWatchdogIfInsideSequenceRoot();
        }

        [JsonProperty]
        public double UserMoonIllumination {
            get => userMoonIllumination;
            set {
                userMoonIllumination = value;
                RaisePropertyChanged();
                CalculateCurrentMoonState();
            }
        }

        [JsonProperty]
        public ComparisonOperator Comparator {
            get => comparator;
            set {
                comparator = value;
                RaisePropertyChanged();
            }
        }

        public double CurrentMoonIllumination {
            get => currentMoonIllumination;
            set {
                currentMoonIllumination = value;
                RaisePropertyChanged();
            }
        }

        public ComparisonOperator[] ComparisonOperators => Enum.GetValues(typeof(ComparisonOperator))
            .Cast<ComparisonOperator>()
            .Where(p => p != ComparisonOperator.EQUALS)
            .Where(p => p != ComparisonOperator.NotEqual)
            .ToArray();

        public override void AfterParentChanged() {
            RunWatchdogIfInsideSequenceRoot();
        }

        public override string ToString() {
            return $"Condition: {nameof(MoonIlluminationCondition)}, " +
                $"CurrentMoonIllumination: {CurrentMoonIllumination}%, Comparator: {Comparator}, UserMoonIllumination: {UserMoonIllumination}%";
        }

        public override bool Check(ISequenceItem? previousItem, ISequenceItem? nextItem) {
            var check = true;
            // See if the moon's illumination is outside of the user's wishes
            switch (Comparator) {
                case ComparisonOperator.LessThan:
                    if (CurrentMoonIllumination < UserMoonIllumination) { check = false; }
                    break;

                case ComparisonOperator.LessThanOrEqual:
                    if (CurrentMoonIllumination <= UserMoonIllumination) { check = false; }
                    break;

                case ComparisonOperator.GreaterThan:
                    if (CurrentMoonIllumination > UserMoonIllumination) { check = false; }
                    break;

                case ComparisonOperator.GreaterThanOrEqual:
                    if (CurrentMoonIllumination >= UserMoonIllumination) { check = false; }
                    break;
            }

            if (!check && IsActive()) {
                Logger.Info($"{nameof(MoonIlluminationCondition)} finished. Current {Comparator} Target: {CurrentMoonIllumination} {Comparator} {UserMoonIllumination}");
            }

            return check;
        }

        private void CalculateCurrentMoonState() {
            var now = DateTime.UtcNow;

            CurrentMoonIllumination = AstroUtil.GetMoonIllumination(now, new ObserverInfo()) * 100;
        }
    }
}