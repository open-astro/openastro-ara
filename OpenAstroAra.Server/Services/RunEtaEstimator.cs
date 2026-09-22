#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Enums;
using OpenAstroAra.Sequencer.Conditions;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.SequenceItem;
using OpenAstroAra.Sequencer.SequenceItem.Imaging;
using OpenAstroAra.Sequencer.SequenceItem.Utility;
using System;

namespace OpenAstroAra.Server.Services;

/// <summary>#1068 — the run's estimated total / remaining duration, computed from the LIVE
/// sequencer tree (the source of the execution model) rather than re-derived by the client from
/// the stored body. Each leaf costs its own <see cref="ISequenceItem.GetEstimatedDuration"/>
/// (TakeExposure = exposure time, WaitForTime = the wait, …) or a flat nominal cost when the
/// instruction reports none (slew, autofocus, dither: crude, but exposure time dominates a real
/// session and the client blends this with the observed elapsed rate anyway). A container
/// multiplies its children by its LoopCondition iterations; the remaining estimate credits the
/// iterations already completed and every leaf already terminal.</summary>
public static class RunEtaEstimator {

    /// <summary>Charged to an instruction whose own estimate is zero. Matches the constant the
    /// client used while this lived there, so the header's numbers did not jump on the move.</summary>
    public const double NominalInstructionSeconds = 15;

    /// <summary>Estimated seconds for the whole plan as it stands (live edits included).</summary>
    public static double EstimateTotalSeconds(ISequenceItem root) {
        ArgumentNullException.ThrowIfNull(root);
        return Full(root);
    }

    /// <summary>Estimated seconds still to run: finished/skipped/disabled/failed leaves cost
    /// nothing, a container's completed loop passes are credited, and the pass in progress
    /// counts only its unfinished children. Never negative.</summary>
    public static double EstimateRemainingSeconds(ISequenceItem root) {
        ArgumentNullException.ThrowIfNull(root);
        return Math.Max(0, Remaining(root));
    }

    private static double Full(ISequenceItem item) {
        // #1080 — a DISABLED subtree never runs in any pass: it is not part of the total either
        // (Remaining already credits it), so total and remaining count the same items. SKIPPED is
        // NOT durable: ResetProgress() turns it back into CREATED between loop passes, so a leaf
        // skipped this pass runs in every later one and stays in the total (Remaining credits it
        // for the pass in progress only).
        if (item.Status == SequenceEntityStatus.DISABLED) {
            return 0;
        }
        if (item is not ISequenceContainer container) {
            return LeafCost(item);
        }
        var children = container.GetItemsSnapshot();
        // #1080 — a ParallelContainer runs its children concurrently: its pass costs the longest
        // child, not the sum.
        double pass = 0;
        if (container is ParallelContainer) {
            foreach (var child in children) {
                pass = Math.Max(pass, Full(child));
            }
        } else {
            foreach (var child in children) {
                pass += Full(child);
            }
        }
        return pass * Iterations(container);
    }

    private static double Remaining(ISequenceItem item) {
        if (IsTerminal(item.Status)) {
            return 0;
        }
        if (item is not ISequenceContainer container) {
            return LeafCost(item);
        }
        if (item.Status == SequenceEntityStatus.CREATED) {
            return Full(item);
        }
        // RUNNING: the pass in progress plus every pass still to come (a parallel block's pass
        // is its longest child, #1080).
        double passRemaining = 0, passFull = 0;
        var parallel = container is ParallelContainer;
        foreach (var child in container.GetItemsSnapshot()) {
            if (parallel) {
                passRemaining = Math.Max(passRemaining, Remaining(child));
                passFull = Math.Max(passFull, Full(child));
            } else {
                passRemaining += Remaining(child);
                passFull += Full(child);
            }
        }
        var passesLeft = Math.Max(0, Iterations(container) - CompletedIterations(container) - 1);
        return passRemaining + passesLeft * passFull;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Estimate boundary: GetEstimatedDuration runs arbitrary instruction code (WaitForTime builds a DateTime from stored fields and can throw on an out-of-range value); an estimate must never turn run-state polling into a 500 — the nominal cost is the honest fallback. CA1031's log-and-recover boundary applies.")]
    private static double LeafCost(ISequenceItem leaf) {
        double seconds;
        try {
            seconds = leaf.GetEstimatedDuration().TotalSeconds;
        } catch (Exception) {
            seconds = 0;
        }
        // #1080 — zero is a real answer from an instruction with a duration model of its own (an
        // unset or invalid ExposureTime of 0; a WaitForTime whose target already passed waits
        // nothing), not "no estimate": only instructions without one get the nominal.
        if (seconds <= 0 && HasOwnDurationModel(leaf)) {
            return 0;
        }
        return seconds > 0 ? seconds : NominalInstructionSeconds;
    }

    // The instructions whose GetEstimatedDuration is a real figure (so zero means zero) rather
    // than the base class's TimeSpan.Zero placeholder (so zero means "no estimate").
    private static bool HasOwnDurationModel(ISequenceItem leaf) =>
        leaf is TakeExposure or WaitForTime or WaitForTimeSpan;

    private static bool IsTerminal(SequenceEntityStatus status) =>
        status is SequenceEntityStatus.FINISHED or SequenceEntityStatus.FAILED
               or SequenceEntityStatus.SKIPPED or SequenceEntityStatus.DISABLED;

    // A container without a (live) LoopCondition runs its children once. Only the first
    // enabled loop condition counts (a container carries at most one in practice); a DISABLED
    // one never gates the container, so it must not multiply.
    private static int Iterations(ISequenceContainer container) {
        if (container is IConditionable c) {
            foreach (var condition in c.GetConditionsSnapshot()) {
                if (condition is LoopCondition { Iterations: > 0 } loop && loop.Status != SequenceEntityStatus.DISABLED) {
                    return loop.Iterations;
                }
            }
        }
        return 1;
    }

    private static int CompletedIterations(ISequenceContainer container) {
        if (container is IConditionable c) {
            foreach (var condition in c.GetConditionsSnapshot()) {
                if (condition is LoopCondition { Iterations: > 0 } loop && loop.Status != SequenceEntityStatus.DISABLED) {
                    return Math.Max(0, loop.CompletedIterations);
                }
            }
        }
        return 0;
    }
}
