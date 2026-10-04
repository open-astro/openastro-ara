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
using OpenAstroAra.Sequencer.SequenceItem.Imaging;
using OpenAstroAra.Sequencer.SequenceItem.Utility;
using OpenAstroAra.Sequencer.Utility;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

// Live run status read off the tree itself. Only some instructions report through
// IProgress (centering, autofocus); TakeExposure and the mount/camera one-shots don't,
// so a run used to keep showing the last reported status ("Smart Focus: shot 1")
// through hours of imaging, with no target name at all. The run worker now also
// polls the running leaf and derives a description + target from it.
public sealed partial class SequencerService {

    // How often the worker re-reads the running leaf between progress reports.
    internal static TimeSpan LiveStatusPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Re-read the running leaf into the run state; true when anything visible changed.</summary>
    private static bool RefreshLiveStatus(RunState run) {
        var leaves = run.Leaves;
        var index = RunningLeafIndex(leaves);
        var changed = run.UpdateProgress(leaves.Count, CountTerminalLeaves(leaves), index);
        if (index is int i) {
            var leaf = leaves[i];
            changed |= run.ApplyRunningLeaf(DescribeLeaf(leaf), TargetNameOf(leaf));
        }
        return changed;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The frame count is cosmetic: a catalog read fault must never stop the live-status poll or the run. Log-and-recover boundary.")]
    private async Task PollLiveStatusAsync(RunState run, Action onChanged, CancellationToken ct) {
        try {
            while (!ct.IsCancellationRequested) {
                await Task.Delay(LiveStatusPollInterval, ct);
                var changed = RefreshLiveStatus(run);
                // Frames this run has filed so far — the number the user watches (it was always 0 the
                // night of 2026-10-02 while 55 frames landed on disk).
                if (run.CaptureSessionId is Guid sid && _frames is not null) {
                    try {
                        changed |= run.SetFramesCaptured(await _frames.CountSessionFramesAsync(sid, ct));
                    } catch (OperationCanceledException) {
                        throw;
                    } catch (Exception) {
                        // next tick
                    }
                }
                if (changed) {
                    onChanged();
                }
            }
        } catch (OperationCanceledException) {
            // run ended
        }
    }

    /// <summary>A human label for a leaf instruction, e.g. "Exposure 300 s LIGHT — 4/60".</summary>
    internal static string DescribeLeaf(ISequenceItem leaf) {
        if (leaf is TakeExposure exposure) {
            var label = string.Create(CultureInfo.InvariantCulture,
                $"Exposure {exposure.ExposureTime:0.##} s {exposure.ImageType}");
            var loop = NearestLoop(leaf);
            if (loop is not null && loop.Iterations > 0) {
                var n = Math.Min(loop.CompletedIterations + 1, loop.Iterations);
                label += string.Create(CultureInfo.InvariantCulture, $" — {n}/{loop.Iterations}");
            }
            return label;
        }
        return Humanize(leaf.GetType().Name);
    }

    /// <summary>
    /// The target a leaf is imaging: the nearest DSO container's target, else the nearest
    /// container carrying an altitude/horizon condition (how client-generated target blocks
    /// are marked). Null outside any target.
    /// </summary>
    internal static string? TargetNameOf(ISequenceItem leaf) => ItemUtility.ResolveTargetName(leaf);

    private static LoopCondition? NearestLoop(ISequenceItem leaf) {
        for (var c = leaf.Parent; c is not null; c = c.Parent) {
            if (c is IConditionable conditionable) {
                foreach (var condition in conditionable.GetConditionsSnapshot()) {
                    if (condition is LoopCondition loop) {
                        return loop;
                    }
                }
            }
        }
        return null;
    }

    // "CenterAndRotate" → "Center and rotate".
    private static string Humanize(string typeName) {
        var words = Regex.Replace(typeName, "(?<=[a-z0-9])(?=[A-Z])", " ").ToLowerInvariant();
        return words.Length == 0 ? typeName : char.ToUpperInvariant(words[0]) + words[1..];
    }
}
