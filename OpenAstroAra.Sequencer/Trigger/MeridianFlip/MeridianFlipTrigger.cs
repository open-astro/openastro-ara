#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Enums;
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
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.Trigger.MeridianFlip {

    /// <summary>
    /// §58 meridian-flip trigger. Re-ported headless from NINA: the decision logic (<see cref="ShouldTrigger"/>
    /// + flip-time computation) is intact and depends only on <see cref="ITelescopeMediator"/> + the profile's
    /// <c>MeridianFlipSettings</c>; the WPF <c>MeridianFlipVM</c> orchestration it used to call is replaced by
    /// the headless <see cref="IMeridianFlipExecutor"/> seam (the §58.4 recovery sequence, its own sub-PR).
    /// </summary>
    [ExportMetadata("Name", "Lbl_SequenceTrigger_MeridianFlipTrigger_Name")]
    [ExportMetadata("Description", "Lbl_SequenceTrigger_MeridianFlipTrigger_Description")]
    [ExportMetadata("Icon", "MeridianFlipSVG")]
    [ExportMetadata("Category", "Lbl_SequenceCategory_Telescope")]
    [Export(typeof(ISequenceTrigger))]
    [JsonObject(MemberSerialization.OptIn)]
    public class MeridianFlipTrigger : SequenceTrigger, IMeridianFlipTrigger, IValidatable {
        private readonly IProfileService profileService;
        private readonly ITelescopeMediator telescopeMediator;
        private readonly IMeridianFlipExecutor executor;

        private DateTime lastFlipTime = DateTime.MinValue;
        private Coordinates? lastFlipCoordinates;

        [ImportingConstructor]
        public MeridianFlipTrigger(IProfileService profileService, ITelescopeMediator telescopeMediator, IMeridianFlipExecutor executor) : base() {
            this.profileService = profileService;
            this.telescopeMediator = telescopeMediator;
            this.executor = executor;
        }

        protected MeridianFlipTrigger(MeridianFlipTrigger cloneMe) : this(cloneMe.profileService, cloneMe.telescopeMediator, cloneMe.executor) {
            CopyMetaData(cloneMe);
        }

        public override object Clone() {
            return new MeridianFlipTrigger(this);
        }

        private IList<string> _issues = new List<string>();

        public IList<string> Issues {
            get => _issues;
            set {
                _issues = ImmutableList.CreateRange(value);
                RaisePropertyChanged();
            }
        }

        private DateTime _latestFlipTime;
        private DateTime _earliestFlipTime;

        public virtual double MinutesAfterMeridian {
            get => profileService.ActiveProfile.MeridianFlipSettings.MinutesAfterMeridian;
            set { }
        }

        public virtual double PauseTimeBeforeMeridian {
            get => profileService.ActiveProfile.MeridianFlipSettings.PauseTimeBeforeMeridian;
            set { }
        }

        public virtual double MaxMinutesAfterMeridian {
            get => profileService.ActiveProfile.MeridianFlipSettings.MaxMinutesAfterMeridian;
            set { }
        }

        public virtual bool UseSideOfPier {
            get => profileService.ActiveProfile.MeridianFlipSettings.UseSideOfPier;
            set { }
        }

        public virtual DateTime LatestFlipTime {
            get => _latestFlipTime;
            protected set {
                _latestFlipTime = value;
                RaisePropertyChanged();
            }
        }

        public virtual DateTime EarliestFlipTime {
            get => _earliestFlipTime;
            protected set {
                _earliestFlipTime = value;
                RaisePropertyChanged();
            }
        }

        public virtual double TimeToMeridianFlip {
            get => telescopeMediator.GetInfo().TimeToMeridianFlip;
            set { }
        }

        public override async Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) {
            var contextCoordinates = ItemUtility.RetrieveContextCoordinates(context);
            var target = contextCoordinates?.Coordinates;
            if (contextCoordinates == null) {
                target = telescopeMediator.GetCurrentPosition();
                Logger.Warning("No target information available for flip. Taking current telescope coordinates instead for the flip.");
            }
            if (target != null && target.RA == 0 && target.Dec == 0) {
                target = telescopeMediator.GetCurrentPosition();
                Logger.Warning("Target coordinates are all zero. Most likely not intended. Taking current telescope coordinates instead for the flip.");
            }

            if (target == null) {
                // No target from the context AND the mount returned no current position (it can disconnect in
                // the window between ShouldTrigger and Execute). Don't slew to nowhere — abort the flip.
                Logger.Error("Meridian Flip - No target coordinates available (telescope returned none). Aborting flip.");
                return;
            }

            var timeToFlip = CalculateMinimumTimeRemaining();
            if (timeToFlip > TimeSpan.FromHours(2)) {
                //Assume a delayed flip when the time is more than two hours and flip immediately
                timeToFlip = TimeSpan.Zero;
            }

            var success = await executor.MeridianFlip(target, timeToFlip, progress, token);
            if (success) {
                // Only record the flip on success — otherwise the ShouldTrigger dedup guard (no re-flip within
                // 11 h) would suppress a retry after a failed slew / plate-solve for the rest of the session.
                lastFlipTime = DateTime.Now;
                lastFlipCoordinates = target;
            } else {
                Logger.Error("Meridian Flip - The flip executor reported failure.");
            }
        }

        public override void AfterParentChanged() {
            lastFlipTime = DateTime.MinValue;
            lastFlipCoordinates = null;
        }

        protected virtual TimeSpan CalculateMinimumTimeRemaining() {
            //Substract delta from maximum to get minimum time
            var delta = MaxMinutesAfterMeridian - MinutesAfterMeridian;
            var time = CalculateMaximumTimeRemaining() - TimeSpan.FromMinutes(delta);
            if (time < TimeSpan.Zero) {
                time = TimeSpan.Zero;
            }
            return time;
        }

        protected virtual TimeSpan CalculateMaximumTimeRemaining() {
            return TimeSpan.FromHours(TimeToMeridianFlip);
        }

        public override bool ShouldTrigger(ISequenceItem? previousItem, ISequenceItem? nextItem) {
            var telescopeInfo = telescopeMediator.GetInfo();

            if (!telescopeInfo.Connected || double.IsNaN(telescopeInfo.TimeToMeridianFlip)) {
                EarliestFlipTime = DateTime.MinValue;
                LatestFlipTime = DateTime.MinValue;
                Logger.Error(telescopeInfo.Connected
                    ? "Meridian Flip - Telescope reports an unknown (NaN) time to meridian flip; cannot evaluate a flip!"
                    : "Meridian Flip - Telescope is not connected to evaluate if a flip should happen!");
                return false;
            }

            if (telescopeInfo.AtPark) {
                EarliestFlipTime = DateTime.MinValue;
                LatestFlipTime = DateTime.MinValue;
                Logger.Info("Meridian Flip - Telescope is parked. Skip flip evaluation");
                return false;
            }

            if (telescopeInfo.AtHome) {
                EarliestFlipTime = DateTime.MinValue;
                LatestFlipTime = DateTime.MinValue;
                Logger.Info("Meridian Flip - Telescope is at home position. Skip flip evaluation");
                return false;
            }

            if (!telescopeInfo.TrackingEnabled) {
                EarliestFlipTime = DateTime.MinValue;
                LatestFlipTime = DateTime.MinValue;
                Logger.Info("Meridian Flip - Telescope is not tracking. Skip flip evaluation");
                return false;
            }

            // When side of pier is disabled - check if the last flip time was less than 11 hours ago and further check if the current position is similar to the last flip position. If all are true, no flip is required.
            if (UseSideOfPier == false && (DateTime.Now - lastFlipTime) < TimeSpan.FromHours(11) && lastFlipCoordinates != null && (lastFlipCoordinates - telescopeInfo.Coordinates).Distance.ArcMinutes < 20) {
                //A flip for the same target is only expected every 12 hours on planet earth and
                Logger.Info($"Meridian Flip - Flip for the current target already happened at {lastFlipTime}. Skip flip evaluation");
                return false;
            }

            var nextInstructionTime = nextItem?.GetEstimatedDuration().TotalSeconds ?? 0;

            //The time to meridian flip reported by the telescope is the latest time for a flip to happen
            var minimumTimeRemaining = CalculateMinimumTimeRemaining();
            var maximumTimeRemaining = CalculateMaximumTimeRemaining();
            var originalMaximumTimeRemaining = maximumTimeRemaining;
            if (PauseTimeBeforeMeridian != 0) {
                //A pause prior to a meridian flip is a hard limit due to equipment obstruction. There is no possibility for a timerange as we have to pause early and wait for meridian to pass
                minimumTimeRemaining = minimumTimeRemaining - TimeSpan.FromMinutes(MinutesAfterMeridian) - TimeSpan.FromMinutes(PauseTimeBeforeMeridian);
                maximumTimeRemaining = minimumTimeRemaining;
            }

            UpdateMeridianFlipTimeTriggerValues(minimumTimeRemaining, originalMaximumTimeRemaining, TimeSpan.FromMinutes(PauseTimeBeforeMeridian), TimeSpan.FromMinutes(MaxMinutesAfterMeridian));

            if (minimumTimeRemaining <= TimeSpan.Zero && maximumTimeRemaining > TimeSpan.Zero) {
                Logger.Info($"Meridian Flip - Remaining Time is between minimum and maximum flip time. Minimum time remaining {minimumTimeRemaining}, maximum time remaining {maximumTimeRemaining}. Flip should happen now");
                return true;
            } else {
                if (UseSideOfPier && telescopeInfo.SideOfPier == PierSide.pierUnknown) {
                    Logger.Error("Side of Pier usage is enabled, however the side of pier reported by the driver is unknown. Ignoring side of pier to calculate the flip time");
                }

                if (UseSideOfPier && telescopeInfo.SideOfPier != PierSide.pierUnknown) {
                    //The minimum time to flip has not been reached yet. Check if a flip is required based on the estimation of the next instruction
                    var noRemainingTime = maximumTimeRemaining <= TimeSpan.FromSeconds(nextInstructionTime);

                    if (noRemainingTime) {
                        // There is no more time remaining. Project the side of pier to that at the time after the flip and check if this flip is required
                        var projectedSiderealTime = Angle.ByHours(AstroUtil.EuclidianModulus(telescopeInfo.SiderealTime + originalMaximumTimeRemaining.TotalHours, 24));
                        var targetSideOfPier = OpenAstroAra.Astrometry.MeridianFlip.ExpectedPierSide(
                            coordinates: telescopeInfo.Coordinates,
                            localSiderealTime: projectedSiderealTime);
                        if (telescopeInfo.SideOfPier == targetSideOfPier) {
                            Logger.Info($"Meridian Flip - Telescope already reports expected pier side {telescopeInfo.SideOfPier}. Automated Flip is not necessary.");
                            return false;
                        } else {
                            Logger.Info($"Meridian Flip - No more remaining time available before flip. Current pier side {telescopeInfo.SideOfPier} - expected pier side {targetSideOfPier}. Flip should happen now");
                            return true;
                        }
                    } else {
                        // There is still time remaining. A flip is likely not required. Double check by checking the current expected side of pier with the actual side of pier
                        var targetSideOfPier = OpenAstroAra.Astrometry.MeridianFlip.ExpectedPierSide(
                            coordinates: telescopeInfo.Coordinates,
                            localSiderealTime: Angle.ByHours(telescopeInfo.SiderealTime));
                        if (telescopeInfo.SideOfPier == targetSideOfPier) {
                            Logger.Info($"Meridian Flip - There is still time remaining - max remaining time {maximumTimeRemaining}, next instruction time {nextInstructionTime} - and the telescope reports expected pier side {telescopeInfo.SideOfPier}. Automated Flip is not necessary.");
                            return false;
                        } else {
                            // When pier side doesn't match the target, but remaining time indicating that a flip happened, the flip seems to have not happened yet and must be done immediately
                            // Only allow delayed flip behavior for the first hour after a flip should've happened
                            var delayedFlip =
                                maximumTimeRemaining <= TimeSpan.FromHours(12)
                                && maximumTimeRemaining
                                    >= (TimeSpan.FromHours(11)
                                        - TimeSpan.FromMinutes(MaxMinutesAfterMeridian)
                                        - TimeSpan.FromMinutes(PauseTimeBeforeMeridian)
                                       );

                            if (delayedFlip) {
                                Logger.Info($"Meridian Flip - Flip seems to not have happened in time as pier side is {telescopeInfo.SideOfPier} but expected to be {targetSideOfPier}. Flip should happen now");
                            }
                            return delayedFlip;
                        }
                    }
                } else {
                    //The minimum time to flip has not been reached yet. Check if a flip is required based on the estimation of the next instruction plus a 2 minute window due to not having side of pier access for delayed flip evaluation
                    var noRemainingTime = maximumTimeRemaining <= (TimeSpan.FromSeconds(nextInstructionTime) + TimeSpan.FromMinutes(2));

                    if (noRemainingTime) {
                        Logger.Info($"Meridian Flip - (Side of Pier usage is disabled) No more remaining time available before flip. Max remaining time {maximumTimeRemaining}, next instruction time {nextInstructionTime}. Flip should happen now");
                        return true;
                    } else {
                        Logger.Info($"Meridian Flip - (Side of Pier usage is disabled) There is still time remaining. Max remaining time {maximumTimeRemaining}, next instruction time {nextInstructionTime}");
                        return false;
                    }
                }
            }
        }

        protected virtual void UpdateMeridianFlipTimeTriggerValues(TimeSpan minimumTimeRemaining, TimeSpan maximumTimeRemaining, TimeSpan pauseBeforeMeridian, TimeSpan maximumTimeAfterMeridian) {
            //Update the FlipTimes
            if (pauseBeforeMeridian == TimeSpan.Zero) {
                EarliestFlipTime = DateTime.Now + minimumTimeRemaining;
                LatestFlipTime = DateTime.Now + maximumTimeRemaining;
            } else {
                EarliestFlipTime = DateTime.Now + maximumTimeRemaining - maximumTimeAfterMeridian - pauseBeforeMeridian;
                LatestFlipTime = DateTime.Now + maximumTimeRemaining - maximumTimeAfterMeridian - pauseBeforeMeridian;
            }
        }

        public override string ToString() {
            return $"Trigger: {nameof(MeridianFlipTrigger)}";
        }

        public virtual bool Validate() {
            var i = new List<string>();
            bool valid = true;
            var telescopeInfo = telescopeMediator.GetInfo();

            if (!telescopeInfo.Connected) {
                i.Add(Loc.Instance["LblTelescopeNotConnected"]);
                valid = false;
            }

            // A misconfigured window (pause_after > max_wait) inverts the flip-time math so the
            // remaining-time fast path never fires. Catch it at setup — Run() validates before Execute().
            if (MinutesAfterMeridian > MaxMinutesAfterMeridian) {
                i.Add($"Meridian flip: minutes after meridian ({MinutesAfterMeridian}) must not exceed max minutes after meridian ({MaxMinutesAfterMeridian}).");
                valid = false;
            }

            // Recenter (camera) + AutoFocusAfterFlip (focuser) preconditions are validated by the
            // IMeridianFlipExecutor, which owns those mediators (§58.4 orchestration sub-PR).
            Issues = i;
            return valid;
        }
    }
}
