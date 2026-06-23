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
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Sequencer.Conditions;
using OpenAstroAra.Sequencer.SequenceItem;
using OpenAstroAra.Sequencer.Trigger;
using OpenAstroAra.Sequencer.Utility;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Linq;

namespace OpenAstroAra.Sequencer.Container {

    /// <summary>
    /// Per-target container exported by NINA's Advanced Sequencer (the bulk of a real
    /// imaging plan). Carries the <see cref="Target"/> (name + coordinates + framing
    /// rotation) and the cached <see cref="ExposureInfoList"/> summary, on top of the
    /// ordinary <see cref="SequentialContainer"/> item/condition/trigger tree.
    ///
    /// Ported as a first-class type so NINA exports round-trip: the JSON <c>$type</c>
    /// <c>NINA.Sequencer.Container.DeepSkyObjectContainer</c> remaps to this class and
    /// deserializes intact instead of degrading to an <see cref="UnknownSequenceContainer"/>.
    /// </summary>
    [ExportMetadata("Name", "Lbl_SequenceItem_Container_DeepSkyObjectContainer_Name")]
    [ExportMetadata("Description", "Lbl_SequenceItem_Container_DeepSkyObjectContainer_Description")]
    [ExportMetadata("Icon", "TelescopeSVG")]
    [ExportMetadata("Category", "Lbl_SequenceCategory_Container")]
    [Export(typeof(ISequenceContainer))]
    [JsonObject(MemberSerialization.OptIn)]
    public class DeepSkyObjectContainer : SequentialContainer, IDeepSkyObjectContainer {
        private readonly IProfileService profileService;
        private readonly Lazy<NighttimeData> nighttimeData;

        [ImportingConstructor]
        public DeepSkyObjectContainer(IProfileService profileService) : base() {
            this.profileService = profileService;
            Target = NewTargetFromProfile();
            // Lazy + the default ExecutionAndPublication mode: computed at most once, and safe to
            // read from multiple threads — without paying the SOFA/NOVAS cost unless something asks.
            nighttimeData = new Lazy<NighttimeData>(() => {
                using var calculator = new NighttimeCalculator(profileService);
                return calculator.Calculate();
            });
        }

        [JsonProperty]
        public InputTarget Target { get; set; }

        /// <summary>
        /// Observer-frame sun/moon rise-and-set for the active profile's site. Computed
        /// lazily (it depends on the location, not the target) so import doesn't pay for
        /// it; callers that don't need it (round-trip, validation) never trigger it.
        /// </summary>
        public NighttimeData NighttimeData => nighttimeData.Value;

        [JsonProperty]
        public bool ExposureInfoListExpanded { get; set; }

        [JsonProperty]
        public IList<ExposureInfo> ExposureInfoList { get; private set; } = new ObservableCollection<ExposureInfo>();

        private InputTarget NewTargetFromProfile() {
            var astrometry = profileService.ActiveProfile.AstrometrySettings;
            return new InputTarget(
                Angle.ByDegree(astrometry.Latitude),
                Angle.ByDegree(astrometry.Longitude),
                astrometry.Horizon);
        }

        public override object Clone() {
            var clone = new DeepSkyObjectContainer(profileService) {
                Icon = Icon,
                Name = Name,
                Category = Category,
                Description = Description,
                Items = new ObservableCollection<ISequenceItem>(Items.Select(i => (ISequenceItem)i.Clone())),
                Triggers = new ObservableCollection<ISequenceTrigger>(Triggers.Select(t => (ISequenceTrigger)t.Clone())),
                Conditions = new ObservableCollection<ISequenceCondition>(Conditions.Select(c => (ISequenceCondition)c.Clone())),
                ExposureInfoListExpanded = ExposureInfoListExpanded,
                // Deep-copy: ExposureInfo.Count is mutable, so a shared reference would let a
                // cloned target's exposure bookkeeping leak back into the original.
                ExposureInfoList = new ObservableCollection<ExposureInfo>(ExposureInfoList.Select(e => e.Clone())),
            };

            // The ctor already built clone.Target from the profile — copy this target's
            // identity onto it rather than allocating a second InputTarget.
            clone.Target.TargetName = Target.TargetName;
            clone.Target.PositionAngle = Target.PositionAngle;
            clone.Target.InputCoordinates = Target.InputCoordinates.Clone();

            foreach (var item in clone.Items) {
                item.AttachNewParent(clone);
            }
            foreach (var condition in clone.Conditions) {
                condition.AttachNewParent(clone);
            }
            foreach (var trigger in clone.Triggers) {
                trigger.AttachNewParent(clone);
            }

            return clone;
        }
    }
}
