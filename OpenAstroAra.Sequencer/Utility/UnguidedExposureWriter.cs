#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Sequencer.Conditions;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.SequenceItem;
using OpenAstroAra.Sequencer.Trigger;
using OpenAstroAra.Sequencer.Trigger.Autofocus;
using OpenAstroAra.Sequencer.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenAstroAra.Sequencer.Utility {

    /// <summary>One imaging loop the ceiling touched: what its light exposure and frame count were and
    /// what they became. The planned integration (old exposure × old remaining frames) is held.</summary>
    public sealed record ExposureRewrite(string LoopName, double OldExposure, double NewExposure, int OldIterations, int NewIterations);

    /// <summary>
    /// #1264 — writes an unguided exposure ceiling (or a guard step) into imaging loops: every LIGHT
    /// exposure longer than the ceiling is shortened to it and the loop's <see cref="LoopCondition"/>
    /// frame count re-derived so the planned total integration holds (frames still to take × old
    /// length ÷ new length, rounded up; frames already taken are kept as they are). The #1068 ETA
    /// estimator reads the same two fields, so progress follows without any extra plumbing.
    /// </summary>
    public static class UnguidedExposureWriter {

        /// <summary>Apply <paramref name="ceilingSeconds"/> to every imaging loop that is a sibling of
        /// <paramref name="after"/> and comes later in <paramref name="parent"/>. Returns what changed
        /// (possibly nothing).</summary>
        public static IReadOnlyList<ExposureRewrite> ApplyCeiling(ISequenceContainer parent, ISequenceItem after, double ceilingSeconds) {
            ArgumentNullException.ThrowIfNull(parent);
            var changes = new List<ExposureRewrite>();
            if (ceilingSeconds <= 0 || double.IsNaN(ceilingSeconds)) {
                return changes;
            }
            var seen = false;
            foreach (var item in parent.GetItemsSnapshot()) {
                if (!seen) {
                    seen = ReferenceEquals(item, after);
                    continue;
                }
                if (item is ISequenceContainer loop) {
                    changes.AddRange(CapLoop(loop, ceilingSeconds));
                }
            }
            return changes;
        }

        /// <summary>Shorten the LIGHT exposures directly inside <paramref name="loop"/> to
        /// <paramref name="ceilingSeconds"/> when they are longer (nested containers are walked too).</summary>
        public static IReadOnlyList<ExposureRewrite> CapLoop(ISequenceContainer loop, double ceilingSeconds) {
            ArgumentNullException.ThrowIfNull(loop);
            var changes = new List<ExposureRewrite>();
            var lights = LightExposures(loop).ToList();
            if (lights.Count > 0 && lights.Max(e => e.ExposureTime) > ceilingSeconds + 1e-9) {
                var change = Rewrite(loop, lights, ceilingSeconds);
                if (change is not null) {
                    changes.Add(change);
                }
            }
            foreach (var nested in loop.GetItemsSnapshot().OfType<ISequenceContainer>()) {
                changes.AddRange(CapLoop(nested, ceilingSeconds));
            }
            return changes;
        }

        /// <summary>Set the LIGHT exposures directly inside <paramref name="loop"/> to
        /// <paramref name="newExposureSeconds"/> (up or down) and re-derive the frame count. The guard's
        /// step. Returns what changed.</summary>
        public static IReadOnlyList<ExposureRewrite> SetLoopExposure(ISequenceContainer loop, double newExposureSeconds) {
            ArgumentNullException.ThrowIfNull(loop);
            var changes = new List<ExposureRewrite>();
            if (newExposureSeconds <= 0 || double.IsNaN(newExposureSeconds)) {
                return changes;
            }
            var lights = LightExposures(loop).Where(e => Math.Abs(e.ExposureTime - newExposureSeconds) > 1e-9).ToList();
            if (lights.Count > 0) {
                var change = Rewrite(loop, lights, newExposureSeconds);
                if (change is not null) {
                    changes.Add(change);
                }
            }
            return changes;
        }

        /// <summary>The LIGHT exposures directly inside <paramref name="loop"/> (blank ImageType is LIGHT).</summary>
        public static IEnumerable<IExposureItem> LightExposures(ISequenceContainer loop) {
            ArgumentNullException.ThrowIfNull(loop);
            return loop.GetItemsSnapshot().OfType<IExposureItem>().Where(IsLight);
        }

        public static bool IsLight(IExposureItem exposure) {
            ArgumentNullException.ThrowIfNull(exposure);
            return string.IsNullOrWhiteSpace(exposure.ImageType) || string.Equals(exposure.ImageType, "LIGHT", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>One rewrite per loop (review of #1318): the frame count is re-derived ONCE from the
        /// loop's longest LIGHT exposure, every LIGHT exposure is set to the new length, and the
        /// loop's exposure-counted autofocus cadence (<see cref="AutofocusAfterExposures"/>, derived by
        /// the run builder for the planned sub so the TIME cadence stays constant) is rescaled by
        /// old/new — otherwise a 300 s → 45 s rewrite would refocus 6.7× as often as planned.</summary>
        private static ExposureRewrite? Rewrite(ISequenceContainer loop, IReadOnlyList<IExposureItem> lights, double newExposure) {
            var oldExposure = lights.Max(e => e.ExposureTime);
            if (oldExposure <= 0) {
                return null;
            }
            var loopCondition = (loop as IConditionable)?.Conditions.OfType<LoopCondition>().FirstOrDefault();
            var oldIterations = loopCondition?.Iterations ?? 0;
            var newIterations = oldIterations;
            if (loopCondition is not null && oldIterations > 0) {
                var completed = Math.Clamp(loopCondition.CompletedIterations, 0, oldIterations);
                var remaining = oldIterations - completed;
                var newRemaining = (int)Math.Ceiling((remaining * oldExposure / newExposure) - 1e-9);
                newIterations = completed + Math.Max(remaining > 0 ? 1 : 0, newRemaining);
                loopCondition.Iterations = newIterations;
            }
            foreach (var exposure in lights) {
                exposure.ExposureTime = newExposure;
            }
            if (loop is ITriggerable triggerable) {
                foreach (var af in triggerable.GetTriggersSnapshot().OfType<AutofocusAfterExposures>()) {
                    if (af.AfterExposures > 0) {
                        af.AfterExposures = Math.Max(1, (int)Math.Round(af.AfterExposures * oldExposure / newExposure));
                    }
                }
            }
            return new ExposureRewrite(loop.Name ?? string.Empty, oldExposure, newExposure, oldIterations, newIterations);
        }
    }
}
