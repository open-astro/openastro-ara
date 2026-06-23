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
using OpenAstroAra.Sequencer.Conditions;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.SequenceItem;
using OpenAstroAra.Sequencer.Trigger;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Linq;

namespace OpenAstroAra.Sequencer.SequenceItem.Imaging {

    /// <summary>
    /// NINA's "Smart Exposure" — the dominant imaging instruction in real plans. It is a
    /// <see cref="SequentialContainer"/> bundling a <c>SwitchFilter</c> + <c>TakeExposure</c>,
    /// a loop condition (the exposure count) and a dither trigger into one collapsible block.
    ///
    /// A container is also an <see cref="ISequenceItem"/>, so this sits inside a parent's
    /// <c>Items</c> list yet deserializes through the container converter (it carries a
    /// <c>Strategy.$type</c>). Ported as a first-class type so NINA exports round-trip
    /// instead of degrading the bulk of every plan to <see cref="UnknownSequenceContainer"/>.
    /// </summary>
    [ExportMetadata("Name", "Lbl_SequenceItem_Imaging_SmartExposure_Name")]
    [ExportMetadata("Description", "Lbl_SequenceItem_Imaging_SmartExposure_Description")]
    [ExportMetadata("Icon", "CameraSVG")]
    [ExportMetadata("Category", "Lbl_SequenceCategory_Imaging")]
    [Export(typeof(ISequenceContainer))]
    [JsonObject(MemberSerialization.OptIn)]
    public class SmartExposure : SequentialContainer {

        [ImportingConstructor]
        public SmartExposure() : base() {
        }

        public override object Clone() {
            var clone = new SmartExposure() {
                Icon = Icon,
                Name = Name,
                Category = Category,
                Description = Description,
                Items = new ObservableCollection<ISequenceItem>(Items.Select(i => (ISequenceItem)i.Clone())),
                Triggers = new ObservableCollection<ISequenceTrigger>(Triggers.Select(t => (ISequenceTrigger)t.Clone())),
                Conditions = new ObservableCollection<ISequenceCondition>(Conditions.Select(c => (ISequenceCondition)c.Clone())),
            };

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
