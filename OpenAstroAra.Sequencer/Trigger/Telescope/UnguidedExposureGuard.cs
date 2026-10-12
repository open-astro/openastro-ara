#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Newtonsoft.Json;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.Interfaces;
using OpenAstroAra.Sequencer.SequenceItem;
using OpenAstroAra.Sequencer.Utility;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.Trigger.Telescope {

    /// <summary>
    /// #1264 — "Unguided Exposure Guard": on a rig without a guider, adapt the imaging loop's exposure
    /// to what the mount is actually holding tonight. After each LIGHT frame the §59.5 analysis lands
    /// through <see cref="IFrameQualityFeed"/>; <see cref="StepDownAfter"/> consecutive frames over
    /// <see cref="EccentricityTolerance"/> step the exposure down one rung (those frames are tagged
    /// <c>trailed</c>, one notification), <see cref="StepUpAfter"/> consecutive frames inside
    /// <see cref="GoodEccentricity"/> while the exposure is below <see cref="MaxExposure"/> step it up
    /// one rung. The loop's frame count is re-derived each time so the planned integration holds. Emitted
    /// by the run builder only when the rig has no guider.
    /// </summary>
    [ExportMetadata("Name", "Lbl_SequenceTrigger_Telescope_UnguidedExposureGuard_Name")]
    [ExportMetadata("Description", "Lbl_SequenceTrigger_Telescope_UnguidedExposureGuard_Description")]
    [ExportMetadata("Icon", "TelescopeSVG")]
    [ExportMetadata("Category", "Lbl_SequenceCategory_Telescope")]
    [Export(typeof(ISequenceTrigger))]
    [JsonObject(MemberSerialization.OptIn)]
    public class UnguidedExposureGuard : SequenceTrigger {

        [ImportingConstructor]
        public UnguidedExposureGuard(IFrameQualityFeed? feed = null) : base() {
            this.feed = feed;
        }

        private UnguidedExposureGuard(UnguidedExposureGuard cloneMe) : base(cloneMe) {
            feed = cloneMe.feed;
            Rungs.Clear();
            foreach (var rung in cloneMe.Rungs) {
                Rungs.Add(rung);
            }
            MaxExposure = cloneMe.MaxExposure;
            EccentricityTolerance = cloneMe.EccentricityTolerance;
            GoodEccentricity = cloneMe.GoodEccentricity;
            StepDownAfter = cloneMe.StepDownAfter;
            StepUpAfter = cloneMe.StepUpAfter;
        }

        private readonly IFrameQualityFeed? feed;

        /// <summary>The exposure ladder the guard steps along (seconds). Empty = the defaults.</summary>
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public Collection<double> Rungs { get; private set; } = new(UnguidedExposureLadder.DefaultRungs.ToList());

        /// <summary>The sky-limited / planned exposure a step-up never exceeds; 0 = no step-ups.</summary>
        [JsonProperty]
        public double MaxExposure { get; set; }

        /// <summary>Median eccentricity above which a frame counts as trailed (issue #1264: 0.5).</summary>
        [JsonProperty]
        public double EccentricityTolerance { get; set; } = 0.5;

        /// <summary>Median eccentricity at or under which a frame counts as well inside tolerance (0.3).</summary>
        [JsonProperty]
        public double GoodEccentricity { get; set; } = 0.3;

        /// <summary>Consecutive trailed frames before stepping down (2).</summary>
        [JsonProperty]
        public int StepDownAfter { get; set; } = 2;

        /// <summary>Consecutive good frames before stepping up (10).</summary>
        [JsonProperty]
        public int StepUpAfter { get; set; } = 10;

        private long lastSeenSequence = -1;
        private int consecutiveBad;
        private int consecutiveGood;
        private readonly List<Guid> badFrames = new();
        private bool pendingStepDown;
        private bool pendingStepUp;
        private bool notifiedStepDown;

        /// <summary>Test/diagnostic readout: the direction the next Execute will move, or null.</summary>
        public string? PendingAction => pendingStepDown ? "down" : pendingStepUp ? "up" : null;

        public override void SequenceBlockInitialize() {
            consecutiveBad = 0;
            consecutiveGood = 0;
            badFrames.Clear();
            pendingStepDown = false;
            pendingStepUp = false;
            notifiedStepDown = false;
            lastSeenSequence = feed?.LatestLight?.Sequence ?? -1;
        }

        public override object Clone() {
            return new UnguidedExposureGuard(this) {
                TriggerRunner = (SequentialContainer)(TriggerRunner?.Clone() ?? new SequentialContainer()),
            };
        }

        public override bool ShouldTrigger(ISequenceItem? previousItem, ISequenceItem? nextItem) {
            if (feed is null || previousItem is not IExposureItem exposure || !UnguidedExposureWriter.IsLight(exposure)) {
                return false;
            }
            var latest = feed.LatestLight;
            if (latest is null || latest.Sequence <= lastSeenSequence) {
                // The §59.5 analysis has not landed for a new frame yet; judge it on the next boundary.
                return false;
            }
            lastSeenSequence = latest.Sequence;
            if (double.IsNaN(latest.Eccentricity)) {
                return false;
            }
            if (latest.Eccentricity > EccentricityTolerance) {
                consecutiveBad++;
                consecutiveGood = 0;
                badFrames.Add(latest.FrameId);
                if (consecutiveBad >= Math.Max(1, StepDownAfter)) {
                    var ladder = UnguidedExposureLadder.Build(Rungs, MaxExposure, null);
                    if (UnguidedExposureLadder.StepDown(ladder, exposure.ExposureTime) < exposure.ExposureTime) {
                        pendingStepDown = true;
                        return true;
                    }
                    // Already on the lowest rung: nothing to step to, but the frames are still flagged.
                    consecutiveBad = 0;
                    pendingStepDown = true; // Execute flags and notifies without changing the exposure.
                    return true;
                }
                return false;
            }
            if (latest.Eccentricity <= GoodEccentricity) {
                consecutiveGood++;
                consecutiveBad = 0;
                badFrames.Clear();
                if (consecutiveGood >= Math.Max(1, StepUpAfter) && MaxExposure > 0 && exposure.ExposureTime < MaxExposure - 1e-9) {
                    var ladder = UnguidedExposureLadder.Build(Rungs, MaxExposure, null);
                    if (UnguidedExposureLadder.StepUp(ladder, exposure.ExposureTime, MaxExposure) > exposure.ExposureTime) {
                        pendingStepUp = true;
                        return true;
                    }
                }
                return false;
            }
            // Between good and tolerance: neither streak advances.
            consecutiveBad = 0;
            consecutiveGood = 0;
            badFrames.Clear();
            return false;
        }

        public override async Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) {
            var down = pendingStepDown;
            var up = pendingStepUp;
            pendingStepDown = false;
            pendingStepUp = false;
            if (feed is null || context is null || (!down && !up)) {
                return;
            }
            var loop = context;
            var current = UnguidedExposureWriter.LightExposures(loop).Select(e => e.ExposureTime).DefaultIfEmpty(0).Max();
            if (current <= 0) {
                return;
            }
            var ladder = UnguidedExposureLadder.Build(Rungs, MaxExposure, null);
            var target = down
                ? UnguidedExposureLadder.StepDown(ladder, current)
                : UnguidedExposureLadder.StepUp(ladder, current, MaxExposure);
            var changes = Math.Abs(target - current) > 1e-9
                ? UnguidedExposureWriter.SetLoopExposure(loop, target)
                : Array.Empty<ExposureRewrite>();
            foreach (var change in changes) {
                Logger.Info(string.Create(CultureInfo.InvariantCulture,
                    $"Unguided exposure guard: '{change.LoopName}' {change.OldExposure:0.#} s × {change.OldIterations} → {change.NewExposure:0.#} s × {change.NewIterations} (stepped {(down ? "down" : "up")})."));
            }
            if (down) {
                var flagged = badFrames.ToArray();
                badFrames.Clear();
                consecutiveBad = 0;
                foreach (var frameId in flagged) {
                    await feed.FlagFrameAsync(frameId, "trailed", token);
                }
                progress?.Report(new ApplicationStatus {
                    Status = changes.Count > 0
                        ? string.Create(CultureInfo.InvariantCulture, $"Stars trailing — unguided exposure stepped down to {target:0.#} s")
                        : "Stars trailing at the shortest exposure — frames tagged 'trailed'",
                });
                if (!notifiedStepDown) {
                    notifiedStepDown = true;
                    var message = changes.Count > 0
                        ? string.Create(CultureInfo.InvariantCulture,
                            $"{flagged.Length} consecutive light frame(s) trailed (median eccentricity over {EccentricityTolerance:0.##}). The exposure was stepped down from {current:0.#} s to {target:0.#} s and the frame count re-derived to hold the planned integration; the trailed frames are tagged 'trailed'.")
                        : string.Create(CultureInfo.InvariantCulture,
                            $"{flagged.Length} consecutive light frame(s) trailed at the shortest rung ({current:0.#} s). Check balance, clutch and polar alignment; the frames are tagged 'trailed'.");
                    await feed.NotifyAsync("Unguided exposure stepped down", message, token);
                }
            } else {
                consecutiveGood = 0;
                if (changes.Count > 0) {
                    progress?.Report(new ApplicationStatus {
                        Status = string.Create(CultureInfo.InvariantCulture, $"Stars round — unguided exposure stepped up to {target:0.#} s"),
                    });
                }
            }
        }
    }
}
