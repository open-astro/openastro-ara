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
        if (item is not ISequenceContainer container) {
            return LeafCost(item);
        }
        double sum = 0;
        foreach (var child in container.GetItemsSnapshot()) {
            sum += Full(child);
        }
        return sum * Iterations(container);
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
        // RUNNING: the pass in progress plus every pass still to come.
        double passRemaining = 0, passFull = 0;
        foreach (var child in container.GetItemsSnapshot()) {
            passRemaining += Remaining(child);
            passFull += Full(child);
        }
        var passesLeft = Math.Max(0, Iterations(container) - CompletedIterations(container) - 1);
        return passRemaining + passesLeft * passFull;
    }

    private static double LeafCost(ISequenceItem leaf) {
        var seconds = leaf.GetEstimatedDuration().TotalSeconds;
        return seconds > 0 ? seconds : NominalInstructionSeconds;
    }

    private static bool IsTerminal(SequenceEntityStatus status) =>
        status is SequenceEntityStatus.FINISHED or SequenceEntityStatus.FAILED
               or SequenceEntityStatus.SKIPPED or SequenceEntityStatus.DISABLED;

    // A container without a LoopCondition runs its children once. Only the first loop
    // condition counts (a container carries at most one in practice).
    private static int Iterations(ISequenceContainer container) {
        if (container is IConditionable c) {
            foreach (var condition in c.Conditions) {
                if (condition is LoopCondition loop && loop.Iterations > 0) {
                    return loop.Iterations;
                }
            }
        }
        return 1;
    }

    private static int CompletedIterations(ISequenceContainer container) {
        if (container is IConditionable c) {
            foreach (var condition in c.Conditions) {
                if (condition is LoopCondition loop && loop.Iterations > 0) {
                    return Math.Max(0, loop.CompletedIterations);
                }
            }
        }
        return 0;
    }
}
