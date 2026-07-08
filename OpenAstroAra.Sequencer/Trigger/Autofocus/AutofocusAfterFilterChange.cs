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
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.Interfaces;
using OpenAstroAra.Sequencer.SequenceItem;
using OpenAstroAra.Sequencer.Utility;
using OpenAstroAra.Sequencer.Validations;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.Trigger.Autofocus {

    /// <summary>
    /// §59.5 "first use of a filter" trigger — fires its <c>TriggerRunner</c> (a
    /// <c>RunAutofocus</c>) before the next LIGHT exposure whenever the wheel's selected filter
    /// differs from the one the last autofocus ran on. With the §59.6 use-current-filter policy
    /// this is how per-filter focus offsets are discovered naturally, without ever swapping to
    /// luminance.
    ///
    /// Headless re-port of NINA's AutofocusAfterFilterChange: the wheel state comes from
    /// <see cref="IFilterWheelMediator"/>, the last-autofocus filter from the
    /// <see cref="IImageHistory"/> seam (unless the profile designates a dedicated autofocus
    /// filter, in which case the locally-tracked filter stands), and the runner content
    /// deserializes from the plan JSON.
    /// </summary>
    [ExportMetadata("Name", "Lbl_SequenceTrigger_AutofocusAfterFilterChangeTrigger_Name")]
    [ExportMetadata("Description", "Lbl_SequenceTrigger_AutofocusAfterFilterChangeTrigger_Description")]
    [ExportMetadata("Icon", "AutoFocusAfterFilterSVG")]
    [ExportMetadata("Category", "Lbl_SequenceCategory_Focuser")]
    [Export(typeof(ISequenceTrigger))]
    [JsonObject(MemberSerialization.OptIn)]
    public class AutofocusAfterFilterChange : SequenceTrigger, IValidatable {

        [ImportingConstructor]
        public AutofocusAfterFilterChange(IFilterWheelMediator? filterWheelMediator = null, IImageHistory? history = null, IProfileService? profileService = null) : base() {
            this.filterWheelMediator = filterWheelMediator;
            this.history = history;
            this.profileService = profileService;
        }

        private AutofocusAfterFilterChange(AutofocusAfterFilterChange cloneMe) : this(cloneMe.filterWheelMediator, cloneMe.history, cloneMe.profileService) {
            CopyMetaData(cloneMe);
        }

        private readonly IFilterWheelMediator? filterWheelMediator;
        private readonly IImageHistory? history;
        private readonly IProfileService? profileService;

        public override object Clone() {
            return new AutofocusAfterFilterChange(this) {
                TriggerRunner = (SequentialContainer)(TriggerRunner?.Clone() ?? new SequentialContainer()),
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

        private string? lastFilter;

        public string? LastAutoFocusFilter {
            get => lastFilter;
            private set {
                lastFilter = value;
                RaisePropertyChanged();
            }
        }

        public override async Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) {
            // Same loud-fail contract as the family: a malformed import clones fine but must not
            // silently skip the autofocus it promised.
            if (TriggerRunner == null) {
                throw new SequenceEntityFailedException("Autofocus-after-filter-change has no trigger runner to execute.");
            }
            await TriggerRunner.Run(progress, token);
        }

        public override void Initialize() {
            LastAutoFocusFilter = filterWheelMediator?.GetInfo()?.SelectedFilter?.Name;
        }

        public override void SequenceBlockInitialize() {
            LastAutoFocusFilter = filterWheelMediator?.GetInfo()?.SelectedFilter?.Name;
        }

        public override bool ShouldTrigger(ISequenceItem? previousItem, ISequenceItem? nextItem) {
            if (nextItem is not IExposureItem exposureItem) { return false; }
            if (exposureItem.ImageType != "LIGHT") { return false; }

            // When no filter is designated the autofocus filter, the authoritative "filter the
            // last AF ran on" is the history record — a manually-run autofocus between exposures
            // must count, or the trigger would re-fire on a filter that is already focused.
            var hasAutofocusFilter = profileService?.ActiveProfile?.FilterWheelSettings?.FilterWheelFilters?.Any(f => f.AutoFocusFilter) == true;
            if (!hasAutofocusFilter) {
                var afPoints = history?.AutofocusPoints;
                if (afPoints is { Count: > 0 }) {
                    LastAutoFocusFilter = afPoints[^1].Filter;
                }
            }

            var currentFilter = filterWheelMediator?.GetInfo()?.SelectedFilter?.Name;
            if (currentFilter == null) {
                // No wheel, or a wheel transiently reporting no selected filter (mid-move /
                // reconnect blip): there is no filter to focus FOR, so firing would waste an
                // autofocus on nothing — and clobbering the reference would fire a second
                // spurious one when the filter reappears. Keep the reference and stay quiet.
                return false;
            }
            if (LastAutoFocusFilter == null) {
                // First sight only anchors — there is no change to react to yet.
                LastAutoFocusFilter = currentFilter;
                return false;
            }
            if (LastAutoFocusFilter == currentFilter) {
                return false;
            }

            // The reference moves only when the trigger actually fires: a meridian-flip veto
            // must leave it pointing at the OLD filter, or the still-owed autofocus for the
            // new one would be forgotten forever (the veto would read new==new next check).
            if (IsVetoedByImminentMeridianFlip(nextItem)) {
                Logger.Warning("Autofocus should be triggered, however the meridian flip is too close to be executed");
                return false;
            }
            LastAutoFocusFilter = currentFilter;
            return true;
        }

        public override string ToString() {
            return $"Trigger: {nameof(AutofocusAfterFilterChange)}";
        }

        public bool Validate() {
            var i = new List<string>();
            // Only the equipment this trigger READS — without a wheel there is no filter change
            // to observe. Camera/focuser readiness is RunAutofocus's concern at execution.
            if (filterWheelMediator?.GetInfo()?.Connected != true) {
                i.Add(Loc.Instance["LblFilterWheelNotConnected"]);
            }
            Issues = i;
            return i.Count == 0;
        }
    }
}
