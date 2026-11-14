#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"


using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Sequencer.Conditions;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.Interfaces;
using OpenAstroAra.Sequencer.SequenceItem;
using OpenAstroAra.Sequencer.SequenceItem.FilterWheel;
using OpenAstroAra.Sequencer.SequenceItem.FlatDevice;
using OpenAstroAra.Sequencer.SequenceItem.Imaging;
using OpenAstroAra.Sequencer.SequenceItem.Utility;
using OpenAstroAra.Server.Contracts;
using System.Collections.Generic;
using System.Linq;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// #1875 — reads the queued plan off a sequence tree: each child container of the body's top is one entry
/// (a target block, or the root children named Flats / Calibration, ADR 0010 rule 4), its loops are the rows.
/// A row's done count is its loop's completed iterations, never the session's frame count. Pure: a view of
/// the tree, not a format (ADR 0018).
/// </summary>
internal static class SequencePlanBuilder {

    internal sealed record Plan(IReadOnlyList<SequencePlanTargetDto> Targets, int FramesDone, string? CurrentBlockName);

    internal static Plan Build(ISequenceContainer top) {
        var targets = new List<SequencePlanTargetDto>();
        string? current = null;
        foreach (var entry in top.GetItemsSnapshot().OfType<ISequenceContainer>()) {
            if (entry.Status == SequenceEntityStatus.DISABLED) {
                continue;
            }
            targets.Add(Entry(entry));
            if (current is null && SequencerService.CollectLeaves(entry).Any(l => l.Status == SequenceEntityStatus.RUNNING)) {
                current = entry.Name;
            }
        }
        return new Plan(targets, targets.Sum(t => t.Rows.Sum(r => r.FramesDone)), current);
    }

    private static SequencePlanTargetDto Entry(ISequenceContainer entry) {
        var calibration = entry.Name is "Flats" or "Calibration";
        var items = entry.GetItemsSnapshot();
        var rows = new List<SequencePlanRowDto>();
        string? filter = null;
        foreach (var item in items) {
            if (item is SwitchFilter sw) {
                filter = sw.Filter?.Name;
            } else if (item is ISequenceContainer loopBox && item.Status != SequenceEntityStatus.DISABLED && LoopOf(loopBox) is { } loop) {
                var exposure = loopBox.GetItemsSnapshot().OfType<TakeExposure>().FirstOrDefault();
                // An unbounded stored count (NINA import, direct write) may be negative: Clamp throws when max < min.
                var total = Math.Max(loop.Iterations, 0);
                rows.Add(new SequencePlanRowDto(filter, exposure?.ExposureTime, total,
                    Math.Clamp(loop.CompletedIterations, 0, total)));
            } else if (calibration && item is FlatPanelFlats flat) {
                rows.Add(new SequencePlanRowDto(filter, null, flat.FrameCount,
                    flat.Status == SequenceEntityStatus.FINISHED ? flat.FrameCount : 0));
            }
        }
        var gain = items.OfType<ISequenceContainer>()
            .SelectMany(l => l.GetItemsSnapshot().OfType<TakeExposure>())
            .Where(e => e.ImageType == "LIGHT")
            .Select(e => (int?)e.Gain)
            .FirstOrDefault();
        var flats = items.OfType<Annotation>().Any(a => a.Text == SequencePlanFlats.EndOfNightMarker) ? SequencePlanFlats.EndOfNight
            : items.OfType<FlatPanelFlats>().Any() && !calibration ? SequencePlanFlats.AfterTarget
            : SequencePlanFlats.None;
        var provider = (entry as IConditionable)?.GetConditionsSnapshot().OfType<TimeCondition>()
            .Select(t => t.SelectedProvider).FirstOrDefault(p => p is not null);
        var (ra, dec) = calibration ? (null, null) : Position(entry);
        return new SequencePlanTargetDto(entry.Name, calibration, ra, dec, gain is -1 ? null : gain, flats,
            provider is null ? null : $"{provider.GetType().FullName}, {provider.GetType().Assembly.GetName().Name}", rows);
    }

    private static LoopCondition? LoopOf(ISequenceContainer c) =>
        (c as IConditionable)?.GetConditionsSnapshot().OfType<LoopCondition>().FirstOrDefault();

    // J2000 degrees: the horizon condition's coordinates (how the client writes a target block), else a
    // DSO container's target. An unset position (0, 0) is none: nothing may slew to RA 0 / Dec 0.
    private static (double? Ra, double? Dec) Position(ISequenceContainer entry) {
        Coordinates? coords = (entry as IConditionable)?.GetConditionsSnapshot().OfType<AboveHorizonCondition>()
            .FirstOrDefault()?.Data.Coordinates.Coordinates;
        coords ??= (entry as IDeepSkyObjectContainer)?.Target?.InputCoordinates?.Coordinates;
        if (coords is null) {
            return (null, null);
        }
        var j2000 = coords.Transform(Epoch.J2000);
        return j2000.RADegrees == 0 && j2000.Dec == 0 ? (null, null) : (j2000.RADegrees, j2000.Dec);
    }
}