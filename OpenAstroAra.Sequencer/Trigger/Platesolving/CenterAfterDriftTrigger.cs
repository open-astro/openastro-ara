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
using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.Interfaces;
using OpenAstroAra.Sequencer.SequenceItem;
using OpenAstroAra.Sequencer.SequenceItem.Platesolving;
using OpenAstroAra.Sequencer.Utility;
using OpenAstroAra.Sequencer.Validations;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.Trigger.Platesolving {

    /// <summary>
    /// NINA's "Center After Drift" (#1195): every <see cref="AfterExposures"/> saved LIGHT frames the
    /// newest one is plate-solved from disk, and when its centre has drifted more than
    /// <see cref="DistanceArcMinutes"/> from the target the trigger re-centres (the §28 capture →
    /// solve → sync → re-slew loop through <see cref="ICenteringExecutor"/>) before the next light.
    ///
    /// Headless re-port: the per-frame solve comes through <see cref="IFrameSolveFeed"/> instead of
    /// NINA's image-save follower. Solving is asynchronous and off the capture path — the newest
    /// measurement may lag the exposure that just finished by a frame, exactly as in NINA. A frame
    /// that does not solve (no solver installed, clouds) is logged once and the trigger stays quiet;
    /// it never fails the run for want of a measurement. Removed in the §0.5 conversion (#242) and
    /// re-ported here so imported plans no longer load it as <c>UnknownSequenceTrigger</c>.
    /// </summary>
    [JsonObject(MemberSerialization.OptIn)]
    public class CenterAfterDriftTrigger : SequenceTrigger, IValidatable {

        public CenterAfterDriftTrigger(IFrameSolveFeed? feed = null, ICenteringExecutor? centeringExecutor = null,
                ITelescopeMediator? telescopeMediator = null, ICameraMediator? cameraMediator = null) : base() {
            this.feed = feed;
            this.centeringExecutor = centeringExecutor;
            this.telescopeMediator = telescopeMediator;
            this.cameraMediator = cameraMediator;
            DistanceArcMinutes = 10;
            AfterExposures = 1;
            Coordinates = new InputCoordinates();
        }

        private CenterAfterDriftTrigger(CenterAfterDriftTrigger cloneMe) : this(cloneMe.feed, cloneMe.centeringExecutor, cloneMe.telescopeMediator, cloneMe.cameraMediator) {
            CopyMetaData(cloneMe);
        }

        private readonly IFrameSolveFeed? feed;
        private readonly ICenteringExecutor? centeringExecutor;
        private readonly ITelescopeMediator? telescopeMediator;
        private readonly ICameraMediator? cameraMediator;

        public override object Clone() {
            return new CenterAfterDriftTrigger(this) {
                TriggerRunner = (SequentialContainer)(TriggerRunner?.Clone() ?? new SequentialContainer()),
                DistanceArcMinutes = DistanceArcMinutes,
                AfterExposures = AfterExposures,
                // Null-safe: a malformed import could deserialize Coordinates as null.
                Coordinates = Coordinates?.Clone() ?? new InputCoordinates(),
                Inherited = Inherited,
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

        private InputCoordinates coordinates = null!;

        /// <summary>The target the drift is measured against. Inherited from the enclosing DSO
        /// container when there is one (NINA), otherwise the client writes it explicitly.</summary>
        [JsonProperty]
        public InputCoordinates Coordinates {
            get => coordinates;
            set {
                coordinates = value;
                RaisePropertyChanged();
            }
        }

        private double distanceArcMinutes;

        /// <summary>Drift (arc minutes) at or beyond which the next light is preceded by a re-centre.</summary>
        [JsonProperty]
        public double DistanceArcMinutes {
            get => distanceArcMinutes;
            set {
                // Keep the old value on a non-positive import rather than throwing (NINA parity).
                if (value > 0.0) {
                    distanceArcMinutes = value;
                    RaisePropertyChanged();
                }
            }
        }

        private int afterExposures;

        /// <summary>Solve every Nth saved light (1 = every light).</summary>
        [JsonProperty]
        public int AfterExposures {
            get => afterExposures;
            set {
                afterExposures = Math.Max(1, value);
                RaisePropertyChanged();
            }
        }

        private bool inherited;

        public bool Inherited {
            get => inherited;
            set {
                inherited = value;
                RaisePropertyChanged();
            }
        }

        private readonly object gate = new();
        private double lastDistanceArcMinutes;
        private long lastSeenSequence = -1;
        private int lightsSinceSolve;
        private Task? solveInFlight;
        // Generation stamp instead of an owned CancellationTokenSource (CA1001: triggers are not
        // disposable): a solve that finishes after its block was torn down is simply discarded.
        private int blockGeneration;
        private bool warnedUnsolved;

        /// <summary>The drift measured by the latest solve (arc minutes); 0 after a re-centre.</summary>
        public double LastDistanceArcMinutes {
            get { lock (gate) { return lastDistanceArcMinutes; } }
            private set {
                lock (gate) { lastDistanceArcMinutes = value; }
                RaisePropertyChanged();
            }
        }

        /// <summary>Test/diagnostic readout: a solve is running.</summary>
        public bool SolveInFlight => Volatile.Read(ref solveInFlight) is { IsCompleted: false };

        public override void SequenceBlockInitialize() {
            ResetSolveState();
            // Frames saved before this block (another target's lights) are not this target's drift.
            lastSeenSequence = feed?.LatestLight?.Sequence ?? -1;
            Interlocked.Increment(ref blockGeneration);
        }

        public override void SequenceBlockTeardown() {
            // Orphan any solve still running for this block: its result must not carry into the next target.
            Interlocked.Increment(ref blockGeneration);
            ResetSolveState();
        }

        private void ResetSolveState() {
            LastDistanceArcMinutes = 0;
            lightsSinceSolve = 0;
            warnedUnsolved = false;
        }

        public override bool ShouldTrigger(ISequenceItem? previousItem, ISequenceItem? nextItem) {
            // Every saved light advances the solve cadence, whatever comes next in the plan.
            if (feed is not null && previousItem is IExposureItem previous && previous.ImageType == "LIGHT") {
                var latest = feed.LatestLight;
                if (latest is not null && latest.Sequence > lastSeenSequence) {
                    lastSeenSequence = latest.Sequence;
                    lightsSinceSolve++;
                    if (lightsSinceSolve >= AfterExposures && !SolveInFlight) {
                        lightsSinceSolve = 0;
                        StartSolve(latest.FrameId);
                    }
                }
            }

            if (nextItem is not IExposureItem exposureItem) { return false; }
            if (exposureItem.ImageType != "LIGHT") { return false; }

            var drift = LastDistanceArcMinutes;
            if (drift >= DistanceArcMinutes) {
                Logger.Info(string.Create(CultureInfo.InvariantCulture, $"Drift exceeded threshold: {drift:0.##} / {DistanceArcMinutes:0.##} arc minutes"));
                if (IsVetoedByImminentMeridianFlip(nextItem)) {
                    Logger.Warning("CenterAfterDrift should be triggered, however the meridian flip is too close to be executed");
                    return false;
                }
                return true;
            }
            return false;
        }

        private void StartSolve(Guid frameId) {
            var generation = Volatile.Read(ref blockGeneration);
            Volatile.Write(ref solveInFlight, Task.Run(() => MeasureDriftAsync(frameId, generation), CancellationToken.None));
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Background measurement boundary: a solver/IO fault must degrade to 'no measurement' (logged), never fault the run loop. Log-and-recover boundary.")]
        private async Task MeasureDriftAsync(Guid frameId, int generation) {
            try {
                var solved = feed is null ? null : await feed.SolveFrameAsync(frameId, CancellationToken.None).ConfigureAwait(false);
                if (Volatile.Read(ref blockGeneration) != generation) {
                    return; // the block ended while solving
                }
                if (solved is null) {
                    if (!warnedUnsolved) {
                        warnedUnsolved = true;
                        Logger.Warning($"Center after drift: light frame {frameId} did not plate-solve — drift is not being measured until a frame solves (is a plate solver installed and configured?)");
                    }
                    return;
                }
                var target = Coordinates?.Coordinates;
                if (target is null) {
                    return;
                }
                var separation = solved.Transform(Epoch.J2000) - target.Transform(Epoch.J2000);
                var distance = separation.Distance.ArcMinutes;
                LastDistanceArcMinutes = distance;
                Logger.Info(string.Create(CultureInfo.InvariantCulture, $"Drift: {distance:0.##} / {DistanceArcMinutes:0.##} arc minutes"));
            } catch (Exception ex) {
                Logger.Error($"Center after drift: measuring frame {frameId} failed", ex);
            }
        }

        public override async Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (centeringExecutor is null) {
                // Prototype-only construction — fail loudly rather than silently skipping the re-centre.
                throw new SequenceEntityFailedException("Center-after-drift is not wired for sequence execution on this daemon.");
            }
            var target = Coordinates?.Coordinates
                ?? ItemUtility.RetrieveContextCoordinates(context)?.Coordinates
                ?? throw new SequenceEntityFailedException("Center-after-drift has no target coordinates.");
            var drift = LastDistanceArcMinutes;
            progress?.Report(new ApplicationStatus {
                Status = string.Create(CultureInfo.InvariantCulture, $"Target drifted {drift:0.#}′ — re-centering"),
            });
            Logger.Info(string.Create(CultureInfo.InvariantCulture, $"Center after drift: {drift:0.##}′ from {target} — re-centering"));
            var converged = await centeringExecutor.CenterAsync(target, progress ?? new Progress<ApplicationStatus>(), token);
            // Whatever the outcome the stale measurement is gone: the next solve decides again. The
            // generation bump also orphans a solve still running for the frame taken BEFORE this
            // re-centre (AfterExposures = 1 starts one on the same boundary that fired); landing late
            // it would write the old drift back and buy a second, needless re-centre.
            Interlocked.Increment(ref blockGeneration);
            ResetSolveState();
            if (!converged) {
                throw new SequenceEntityFailedException("Center-after-drift: centering did not converge within the profile's threshold/attempts.");
            }
        }

        public override void AfterParentChanged() {
            if (Parent is null) {
                SequenceBlockTeardown();
                return;
            }
            var contextCoordinates = ItemUtility.RetrieveContextCoordinates(Parent);
            if (contextCoordinates is not null) {
                Coordinates ??= new InputCoordinates();
                Coordinates.Coordinates = contextCoordinates.Coordinates;
                Inherited = true;
            } else {
                Inherited = false;
            }
            Validate();
            if (Parent.Status == SequenceEntityStatus.RUNNING) {
                SequenceBlockInitialize();
            }
        }

        public bool Validate() {
            var i = new List<string>();
            if (Coordinates?.Coordinates is null) {
                i.Add("Center after drift: no target coordinates");
            }
            if (cameraMediator?.GetInfo() is { Connected: false }) {
                i.Add("Camera not connected");
            }
            if (telescopeMediator?.GetInfo() is { Connected: false }) {
                i.Add("Mount not connected");
            }
            Issues = i;
            return i.Count == 0;
        }

        public override string ToString() {
            return string.Create(CultureInfo.InvariantCulture, $"Trigger: {nameof(CenterAfterDriftTrigger)}, DistanceArcMinutes: {DistanceArcMinutes}, AfterExposures: {AfterExposures}");
        }
    }
}
