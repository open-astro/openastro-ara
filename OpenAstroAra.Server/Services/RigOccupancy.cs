#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>What a routine holds while it runs (#2141). A busy question names the holdings it cares about.</summary>
[Flags]
public enum RigHolding {
    None = 0,
    Mount = 1,
    MainCamera = 2,
    GuideCamera = 4,
    Focuser = 8,
    /// <summary>Writes frames to the storage drive.</summary>
    Disk = 16,
    Any = Mount | MainCamera | GuideCamera | Focuser | Disk,
}

/// <summary>How one routine answered <see cref="RigOccupancy.StopAsync"/> (#2142).</summary>
/// <param name="Name">The routine's name, as in a busy reason.</param>
/// <param name="Holds">What it holds; a ladder stops the mount again after a routine that holds it.</param>
/// <param name="Stopped">Its stop returned within the bound. A stop returns once the routine has ended or can no longer
/// move anything (a polar alignment stuck in a hung call is fenced at its takeover's bound, #2066).</param>
/// <param name="FailedRung">Emergency Stop's <c>failed_rungs</c> token for this routine.</param>
/// <param name="Fault">What its stop threw, or the timeout; null exactly when <paramref name="Stopped"/>.</param>
public sealed record RoutineStop(string Name, RigHolding Holds, bool Stopped, string FailedRung, Exception? Fault);

/// <summary>
/// Rig Occupancy (#2141, ADR 0025): one register of what is running on the rig and what each routine holds.
/// A routine registers once, at composition, with a reading of its own running state; the register keeps no copy
/// of that state, so it cannot drift from the routine. Every busy question asks <see cref="Holder"/>; every stop
/// ladder asks <see cref="StopAsync"/> (#2142).
/// </summary>
public sealed class RigOccupancy {
    /// <summary>The Session's name in a busy reason ("a sequence is running").</summary>
    public const string Session = "a sequence";

    private sealed record Routine(string Name, RigHolding Holds, Func<bool> IsRunning,
        Func<string, CancellationToken, Task>? Stop, string FailedRung);

    // Filled once in the composition root, then only read; Register replaces the array whole.
    private volatile Routine[] _routines = [];

    /// <summary>Adds a routine. <paramref name="name"/> reads in a reason: "{name} is running". The first running
    /// routine in registration order names the refusal, so register the widest one (the Session) first: a centering
    /// that a Session runs then reads as the Session.</summary>
    /// <param name="stop">#2142 — ends the routine and returns once it has ended or can no longer move anything: its
    /// return is the routine's confirmation, so it must not return earlier (the register bounds it). Its first argument
    /// names the ladder (a <see cref="MountStopReasons"/> value). Null: no ladder stops it through the register.</param>
    /// <param name="failedRung">Emergency Stop's <c>failed_rungs</c> token when the stop does not take effect;
    /// default <c>stop_</c> and the name in snake case.</param>
    public RigOccupancy Register(string name, RigHolding holds, Func<bool> isRunning,
            Func<string, CancellationToken, Task>? stop = null, string? failedRung = null) {
        _routines = [.. _routines, new Routine(name, holds, isRunning, stop, failedRung ?? "stop_" + name.Replace(' ', '_'))];
        return this;
    }

    /// <summary>The Session: a run that is not terminal (starting, running, paused, awaiting the person or
    /// aborting), or a catalog session still open. The catalog alone misses a run whose session failed to open.
    /// It registers no stop: each ladder ends or pauses runs in its own step (Emergency Stop aborts them, the safety
    /// park pauses them), so a park that asks for the mount does not abort the run it should pause.</summary>
    public RigOccupancy RegisterSession(ActiveRunSessionRegistry runs, Func<ISequencerService?> sequencer) =>
        Register(Session, RigHolding.Any, () => runs.HasAny || sequencer()?.HasUnfinishedRun == true);

    /// <summary>The Polar Align Routine: its own <see cref="IPolarAlignService.IsActive"/>. Its stop is the takeover
    /// (#2066): the run ends and leaves the mount to the ladder, which stops or parks it itself.</summary>
    public RigOccupancy RegisterPolarAlign(Func<IPolarAlignService?> polarAlign) =>
        Register("polar alignment", RigHolding.Mount | RigHolding.MainCamera | RigHolding.GuideCamera,
            () => polarAlign()?.IsActive == true,
            (by, ct) => polarAlign() is { } routine ? routine.StopForTakeoverAsync(by, ct) : Task.CompletedTask,
            failedRung: "stop_polar_align");

    /// <summary>Center: a centering is under way. Its stop cancels it and waits for its end.</summary>
    public RigOccupancy RegisterCenter(Func<ICenteringService?> centering) =>
        Register("centering", RigHolding.Mount | RigHolding.MainCamera,
            () => centering()?.IsCentering == true,
            (_, ct) => centering()?.StopAsync(ct) ?? Task.CompletedTask);

    /// <summary>#2143 — filter-offset calibration: a background job of autofocus runs on the main camera and the focuser.
    /// Register it before autofocus: a stop then ends the job before it starts its next run, and a refusal during the job
    /// names the calibration. Its stop cancels the job and waits for it to end.</summary>
    public RigOccupancy RegisterFilterOffsetCalibration(Func<FilterOffsetCalibrationService?> calibration) =>
        Register("filter-offset calibration", RigHolding.MainCamera | RigHolding.Focuser,
            () => calibration()?.IsRunning == true,
            (_, ct) => CancelThenWaitAsync(() => calibration()?.TryCancel(), () => calibration()?.IsRunning == true, ct),
            failedRung: "stop_filter_offset_calibration");

    /// <summary>#2143 — autofocus: a run in progress. Its stop cancels the run and waits for it to end (the run puts the
    /// focuser back where it started first).</summary>
    public RigOccupancy RegisterAutofocus(Func<AutofocusRunTracker?> autofocus) =>
        Register("autofocus", RigHolding.MainCamera | RigHolding.Focuser,
            () => autofocus()?.IsRunning == true,
            (_, ct) => CancelThenWaitAsync(() => autofocus()?.TryCancel(), () => autofocus()?.IsRunning == true, ct),
            failedRung: "cancel_autofocus");

    /// <summary>#2143 — the Bahtinov focus readout: a camera loop on the main camera. Its stop waits for the frame in
    /// flight to drain.</summary>
    public RigOccupancy RegisterBahtinov(Func<IBahtinovFocusService?> bahtinov) =>
        Register("the Bahtinov focus readout", RigHolding.MainCamera,
            () => bahtinov()?.IsActive == true,
            (_, _) => bahtinov()?.StopAsync() ?? Task.CompletedTask,
            failedRung: "stop_bahtinov");

    /// <summary>#2143 — Guide Focus: a camera loop on the Guide Camera (its frames come through the guider, so stopping
    /// guiding does not end it). Its stop waits for the loop to end.</summary>
    public RigOccupancy RegisterGuideFocus(Func<IGuideFocusService?> guideFocus) =>
        Register("guide-camera focus", RigHolding.GuideCamera,
            () => guideFocus()?.IsActive == true,
            (_, _) => guideFocus()?.StopAsync() ?? Task.CompletedTask,
            failedRung: "stop_guide_focus");

    /// <summary>#2143 — the rotation readout: a camera loop on the main camera, or its Confirm frame. Its stop waits
    /// for the solve in flight to drain.</summary>
    public RigOccupancy RegisterRotationAssist(Func<IRotationAssistService?> rotation) =>
        Register("the rotation readout", RigHolding.MainCamera,
            () => rotation()?.IsBusy == true,
            (_, _) => rotation()?.StopAsync() ?? Task.CompletedTask,
            failedRung: "stop_rotation_assist");

    /// <summary>A stop for a routine whose cancel only asks: ask, then wait until its reading says it ended.</summary>
    private static async Task CancelThenWaitAsync(Action cancel, Func<bool> isRunning, CancellationToken ct) {
        cancel();
        while (isRunning()) {
            await Task.Delay(20, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The name of the first running routine that holds any of <paramref name="holding"/>, or null when
    /// none does.</summary>
    public string? Holder(RigHolding holding = RigHolding.Any) {
        foreach (var routine in _routines) {
            if ((routine.Holds & holding) != 0 && routine.IsRunning()) return routine.Name;
        }
        return null;
    }

    /// <summary>#2142 — stops every running routine that holds any of <paramref name="holding"/> and has a stop, the
    /// mount's holders first, one after the other. Each stop has <paramref name="bound"/>; past it, or on a fault, the
    /// next routine is still stopped. Never throws. Returns one entry per routine it stopped, in order: whether its stop
    /// returned in time.</summary>
    /// <param name="by">The ladder, for the routine's log (a <see cref="MountStopReasons"/> value).</param>
    public async Task<IReadOnlyList<RoutineStop>> StopAsync(RigHolding holding, string by, TimeSpan bound) {
        var stops = new List<RoutineStop>();
        var running = _routines
            .Where(r => r.Stop is not null && (r.Holds & holding) != 0 && IsRunningQuietly(r))
            .OrderBy(r => (r.Holds & RigHolding.Mount) == 0)
            .ToList();
        foreach (var routine in running) {
            var fault = await StopQuietlyAsync(routine, by, bound).ConfigureAwait(false);
            stops.Add(new RoutineStop(routine.Name, routine.Holds, fault is null, routine.FailedRung, fault));
        }
        return stops;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A stop ladder must reach every routine: a fault is returned in the routine's entry, never thrown.")]
    private static async Task<Exception?> StopQuietlyAsync(Routine routine, string by, TimeSpan bound) {
        using var cts = new CancellationTokenSource(bound);
        try {
            // A dedicated thread (as Emergency Stop's rungs, #1985): a stop that blocks before its first await does not
            // hold the ladder past the bound, and a starved pool does not delay its start.
            await Task.Factory.StartNew(() => routine.Stop!(by, cts.Token), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap().WaitAsync(bound).ConfigureAwait(false);
            return null;
        } catch (Exception ex) {
            return ex;
        } finally {
            // WaitAsync can time out before the source's timer fires: cancel here, so a stop left running past the bound
            // sees its token cancelled before the source is disposed (else a Center's wait for its end polls forever).
            await cts.CancelAsync().ConfigureAwait(false);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A reading that throws cannot say the routine is idle: it counts as running, so the ladder stops it.")]
    private static bool IsRunningQuietly(Routine routine) {
        try {
            return routine.IsRunning();
        } catch (Exception) {
            return true;
        }
    }
}
