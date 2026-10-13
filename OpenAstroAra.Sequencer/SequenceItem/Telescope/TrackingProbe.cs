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
using OpenAstroAra.Sequencer.Utility;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.SequenceItem.Telescope {

    /// <summary>
    /// #1264 — "Tracking Probe": on a rig without a guider, measure at run start how long a sub the
    /// mount can hold. Takes the ladder of exposures (<see cref="Rungs"/>, capped at the planned
    /// length <see cref="MaxExposure"/> and at twice the resolved prior — the body's own
    /// <see cref="PriorSeconds"/>, else the stored calibration, else the mount's spec sheet, see
    /// <see cref="ITrackingProbeExecutor.ResolvePriorWithSourceAsync"/>) at the target, through
    /// <see cref="ITrackingProbeExecutor"/>, and writes the measured ceiling into the
    /// imaging loops that follow it in the same block: their LIGHT exposures become
    /// <c>min(planned, ceiling)</c> and their frame counts are re-derived so the planned integration
    /// holds. An inconclusive ladder (fewer than two usable rungs, none failing) keeps the prior
    /// (applied as the ceiling when known) and says so; a first rung that already trails is itself the
    /// ceiling. The run builder emits this only when the rig has no guider; with a guider it never appears.
    /// </summary>
    [ExportMetadata("Name", "Lbl_SequenceItem_Telescope_TrackingProbe_Name")]
    [ExportMetadata("Description", "Lbl_SequenceItem_Telescope_TrackingProbe_Description")]
    [ExportMetadata("Icon", "TelescopeSVG")]
    [ExportMetadata("Category", "Lbl_SequenceCategory_Telescope")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class TrackingProbe : SequenceItem {

        [ImportingConstructor]
        public TrackingProbe(ITrackingProbeExecutor? executor = null) {
            this.executor = executor;
        }

        private readonly ITrackingProbeExecutor? executor;

        /// <summary>The exposure ladder in seconds. Empty = the defaults (10, 30, 60, 120). Get-only so
        /// Newtonsoft populates it in place on deserialization (a saved body's rungs replace the defaults).</summary>
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public Collection<double> Rungs { get; private set; } = new(UnguidedExposureLadder.DefaultRungs.ToList());

        private double maxExposure;

        /// <summary>The sky-limited / planned exposure the ladder is capped at; 0 = uncapped.</summary>
        [JsonProperty]
        public double MaxExposure {
            get => maxExposure;
            set {
                maxExposure = value;
                RaisePropertyChanged();
            }
        }

        private double? priorSeconds;

        /// <summary>#1264 Imaging → Unguided — a rung is "trailed" above this median eccentricity; the
        /// run builder copies the profile's trailed bar here so the probe and the guard judge alike.</summary>
        [JsonProperty]
        public double EccentricityTolerance { get; set; } = 0.5;

        /// <summary>The mount's spec-sheet ceiling (seconds), null for an unknown mount.</summary>
        [JsonProperty]
        public double? PriorSeconds {
            get => priorSeconds;
            set {
                priorSeconds = value;
                RaisePropertyChanged();
            }
        }

        private double? ceilingSeconds;

        /// <summary>The ceiling this probe measured (set by Execute; null before it ran or when the
        /// measurement was inconclusive). Serialized so a run body saved after the probe keeps it.</summary>
        [JsonProperty]
        public double? CeilingSeconds {
            get => ceilingSeconds;
            set {
                ceilingSeconds = value;
                RaisePropertyChanged();
            }
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (executor is null) {
                throw new SequenceEntityFailedException("The tracking probe is not wired for sequence execution on this daemon.");
            }
            // The executor resolves the prior and names its source: the body's own value first, then the
            // profile's stored ceiling from the last probe (so run 2 does not climb the whole ladder again,
            // review of #1318), then the mount's spec sheet (#1264 stage 1).
            var (prior, priorSource) = await executor.ResolvePriorWithSourceAsync(PriorSeconds, token);
            var ladder = UnguidedExposureLadder.Build(Rungs, MaxExposure, prior);
            var request = new TrackingProbeRequest(ladder, MaxExposure, prior, ItemUtility.ResolveTargetName(Parent), priorSource,
                EccentricityTolerance > 0 ? EccentricityTolerance : 0.5);
            var result = await executor.ProbeAsync(request, progress, token);
            var ceiling = result.CeilingSeconds ?? prior;
            CeilingSeconds = result.CeilingSeconds;
            if (ceiling is not double applied || applied <= 0) {
                Logger.Info($"Tracking probe: {result.Summary}; no ceiling to apply.");
                progress?.Report(new ApplicationStatus { Status = $"Tracking probe: {result.Summary}" });
                return;
            }
            var changes = Parent is null
                ? Array.Empty<ExposureRewrite>()
                : UnguidedExposureWriter.ApplyCeiling(Parent, this, applied);
            foreach (var change in changes) {
                Logger.Info(string.Create(CultureInfo.InvariantCulture,
                    $"Tracking probe: '{change.LoopName}' {change.OldExposure:0.#} s × {change.OldIterations} → {change.NewExposure:0.#} s × {change.NewIterations} (unguided ceiling {applied:0.#} s)."));
            }
            var source = result.CeilingSeconds is null ? $"prior from {priorSource ?? "the body"}" : "measured";
            progress?.Report(new ApplicationStatus {
                Status = string.Create(CultureInfo.InvariantCulture,
                    $"Unguided exposure ceiling {applied:0.#} s ({source}); {changes.Count} imaging loop(s) adjusted"),
            });
        }

        /// <summary>One frame per rung plus a few seconds of download and analysis each.</summary>
        public override TimeSpan GetEstimatedDuration() {
            var ladder = UnguidedExposureLadder.Build(Rungs, MaxExposure, PriorSeconds);
            return TimeSpan.FromSeconds(ladder.Sum() + (ladder.Count * 5));
        }

        public override object Clone() {
            var clone = new TrackingProbe(executor) {
                Icon = Icon,
                Name = Name,
                Category = Category,
                Description = Description,
                MaxExposure = MaxExposure,
                PriorSeconds = PriorSeconds,
                CeilingSeconds = CeilingSeconds,
            };
            clone.Rungs.Clear();
            foreach (var rung in Rungs) {
                clone.Rungs.Add(rung);
            }
            return clone;
        }

        public override string ToString() {
            return $"Category: {Category}, Item: {nameof(TrackingProbe)}, Rungs: {string.Join("/", Rungs)}, MaxExposure: {MaxExposure}, Prior: {PriorSeconds}";
        }
    }
}
