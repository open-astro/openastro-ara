#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>How a ladder step ended. <see cref="Cancelled"/> is only a warm-up the caller abandoned.</summary>
public enum LadderOutcome { Done, Skipped, Failed, Cancelled }

/// <summary>One step's outcome and its sentence for the summary notification (ends with a space).</summary>
public readonly record struct LadderStep(LadderOutcome Outcome, string Text);

/// <summary>Where the mount stood when <see cref="ShutdownLadder.RestMountAsync"/> ended.</summary>
public enum MountRest {
    /// <summary>The mount is parked.</summary>
    Parked,
    /// <summary>The mount cannot park or did not, and tracking was switched off.</summary>
    TrackingOff,
    /// <summary>No mount, an unreadable one, or a move that failed: the mount may still be moving.</summary>
    Unknown,
}

/// <summary>The mount step's result: its <see cref="Step"/> and where the mount <see cref="Rest"/>s.</summary>
public readonly record struct MountRestResult(LadderStep Step, MountRest Rest);

/// <summary>The devices <see cref="ShutdownLadder.DisconnectAsync"/> takes down before the camera.</summary>
public enum AncillaryDevice { FilterWheel, Focuser, Rotator, FlatPanel }

/// <summary>
/// §58.12 — the steps that put a rig to bed, shared by <see cref="UnattendedShutdownService"/> and
/// <see cref="EndNightService"/>: stop the guider, park the mount (or stop tracking), disconnect the
/// filter wheel, focuser, rotator and flat panel, warm the cooler and switch it off, then disconnect
/// the camera. Each step is best-effort: it never throws and says in its <see cref="LadderStep"/> how
/// it ended. The caller owns the order, the boundaries between steps and the summary.
/// </summary>
public sealed partial class ShutdownLadder {

    /// <summary>The set-point the warm-up ramps toward. +10 °C is a safely
    /// "warm" TEC target for any climate; the cooler turns OFF once reached, so
    /// the sensor finishes settling to true ambient passively.</summary>
    internal const double WarmTargetC = 10.0;

    private readonly IProfileStore? _profiles;
    private readonly IGuiderMediator? _guider;
    private readonly ITelescopeMediator? _telescope;
    private readonly ICameraService? _camera;
    private readonly IFilterWheelService? _filterWheel;
    private readonly IFocuserService? _focuser;
    private readonly IRotatorService? _rotator;
    private readonly IFlatDeviceService? _flatDevice;
    private readonly RigOccupancy? _occupancy;
    private readonly ILogger _logger;

    // ─── test seams (the ClientSessionService knob pattern) ───
    /// <summary>Time scale for the warm-up loop: the set-point steps ramp °C
    /// once per (1 minute / scale). Production stays 1.0 — the camera sees the
    /// profile's true °C/min; tests raise it to compress minutes into ms.</summary>
    internal double WarmTimeScale { get; set; } = 1.0;
    /// <summary>Hard cap on the whole warm-up, per §58.12 step 5.</summary>
    internal TimeSpan WarmHardCap { get; set; } = TimeSpan.FromMinutes(30);
    /// <summary>Cap on the park attempt (mirrors §58.9 SafeRest's bound).</summary>
    internal TimeSpan ParkTimeout { get; set; } = TimeSpan.FromSeconds(90);
    /// <summary>Cap on the Find Home attempt.</summary>
    internal TimeSpan FindHomeTimeout { get; set; } = TimeSpan.FromSeconds(120);
    /// <summary>#2066, #2142 — cap on ending each routine before the Find Home or park (polar alignment's own end is
    /// bounded too, under a minute for a run stuck in a hung call). Past it the step goes on: the mount refuses the move
    /// while polar alignment still holds it, and the park step stops tracking instead.</summary>
    internal TimeSpan RoutineStopTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public ShutdownLadder(
            IProfileStore? profiles = null,
            IGuiderMediator? guider = null,
            ITelescopeMediator? telescope = null,
            ICameraService? camera = null,
            IFilterWheelService? filterWheel = null,
            IFocuserService? focuser = null,
            IRotatorService? rotator = null,
            IFlatDeviceService? flatDevice = null,
            ILogger? logger = null,
            RigOccupancy? occupancy = null) {
        _profiles = profiles;
        _guider = guider;
        _telescope = telescope;
        _camera = camera;
        _filterWheel = filterWheel;
        _focuser = focuser;
        _rotator = rotator;
        _flatDevice = flatDevice;
        _occupancy = occupancy;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>#2066 (ARA-2), #2142 — the mount steps' first move: every routine in <see cref="RigOccupancy"/> ends
    /// through its own stop, the mount's holders first. A running polar alignment holds the mount, and the mediator refuses
    /// a Find Home or park while it does: it ends through its takeover (it stops its motor, leaves the mount to this step
    /// and gives the mount back). A Center is cancelled, so it does not re-slew. Each stop is bounded by
    /// <see cref="RoutineStopTimeout"/>; a routine still running is logged and the step goes on.</summary>
    private async Task EndRoutinesFirstAsync() {
        if (_occupancy is null) {
            return;
        }
        foreach (var stop in await _occupancy.StopAsync(RigHolding.Any, MountStopReasons.Shutdown, RoutineStopTimeout).ConfigureAwait(false)) {
            if (!stop.Stopped) {
                LogStepFailed(stop.FailedRung, stop.Fault!);
            }
        }
    }

    /// <summary>Stop the guider. GetInfo is only an optimization to skip a
    /// disconnected guider — if the read itself throws, the state is
    /// UNKNOWN and the stop is still attempted (an unknown guider quietly
    /// correcting a safe-rested mount is the failure mode, not a wasted
    /// no-op stop call).</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort by design: a fault in one step must not stop the rest of the shutdown.")]
    public async Task<LadderStep> StopGuidingAsync() {
        if (_guider is null) return new(LadderOutcome.Skipped, string.Empty);
        bool? guiderConnected = null;
        try {
            guiderConnected = _guider.GetInfo().Connected;
        } catch (Exception ex) {
            LogStepFailed("guider-info", ex);
        }
        if (guiderConnected == false) return new(LadderOutcome.Skipped, string.Empty);
        try {
            await _guider.StopGuiding(CancellationToken.None).ConfigureAwait(false);
            return new(LadderOutcome.Done, "Guider stopped. ");
        } catch (Exception ex) {
            LogStepFailed("stop-guider", ex);
            return new(LadderOutcome.Failed, "Guider stop FAILED. ");
        }
    }

    /// <summary>Find Home, for a connected, unparked mount that can. A parked mount refuses the move
    /// and is already home; a mount that cannot find home is skipped.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort by design: a fault in one step must not stop the rest of the shutdown.")]
    public async Task<LadderStep> FindHomeAsync() {
        if (_telescope is null) return new(LadderOutcome.Skipped, string.Empty);
        try {
            var mount = _telescope.GetInfo();
            if (!mount.Connected || !mount.CanFindHome || mount.AtPark) return new(LadderOutcome.Skipped, string.Empty);
            await EndRoutinesFirstAsync().ConfigureAwait(false);
            using var cts = new CancellationTokenSource(FindHomeTimeout);
            var found = await _telescope.FindHome(new Progress<OpenAstroAra.Core.Model.ApplicationStatus>(), cts.Token).ConfigureAwait(false);
            return found
                ? new(LadderOutcome.Done, "Mount found home. ")
                : new(LadderOutcome.Failed, "Mount did not find home. ");
        } catch (Exception ex) {
            LogStepFailed("find-home", ex);
            return new(LadderOutcome.Failed, "Mount find-home FAILED. ");
        }
    }

    /// <summary>Park the mount (or stop tracking). Same moves as §58.9 SafeRest, including its two-stage
    /// shape: the park attempt gets its OWN catch so a park that fails by THROWING (the 90 s timeout
    /// cancels the token) still falls through to the tracking-stop — a tracking, unattended
    /// mount is the exact outcome this feature exists to prevent. Both moves are idempotent when safe
    /// rest already ran.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort by design: a fault in one step must not stop the rest of the shutdown.")]
    public async Task<MountRestResult> RestMountAsync() {
        if (_telescope is null) return new(new(LadderOutcome.Skipped, string.Empty), MountRest.Unknown);
        try {
            OpenAstroAra.Equipment.Equipment.MyTelescope.TelescopeInfo? mount = null;
            try {
                mount = _telescope.GetInfo();
            } catch (Exception ex) {
                LogStepFailed("mount-info", ex);
            }
            if (mount is null) {
                // State unreadable — the one thing still worth trying blind
                // is the tracking stop: an unattended TRACKING mount is the
                // outcome this feature exists to prevent, and a stop sent
                // to a parked/disconnected mount is a harmless no-op.
                _telescope.SetTrackingEnabled(false);
                return new(new(LadderOutcome.Failed, "Mount state unreadable — tracking stop attempted. "), MountRest.Unknown);
            }
            if (mount.Connected) {
                await EndRoutinesFirstAsync().ConfigureAwait(false);
            }
            if (mount.CanPark && !mount.AtPark) {
                var parked = false;
                try {
                    using var parkCts = new CancellationTokenSource(ParkTimeout);
                    var progress = new Progress<OpenAstroAra.Core.Model.ApplicationStatus>();
                    parked = await _telescope.ParkTelescope(progress, parkCts.Token).ConfigureAwait(false);
                } catch (Exception ex) {
                    LogStepFailed("park-mount", ex);
                }
                if (parked) {
                    return new(new(LadderOutcome.Done, "Mount parked. "), MountRest.Parked);
                }
                var stopped = _telescope.SetTrackingEnabled(false);
                return new(new(LadderOutcome.Failed, "Park failed — tracking stopped instead. "),
                    stopped ? MountRest.TrackingOff : MountRest.Unknown);
            }
            if (mount.AtPark) {
                return new(new(LadderOutcome.Done, "Mount already parked. "), MountRest.Parked);
            }
            if (mount.Connected) {
                var stopped = _telescope.SetTrackingEnabled(false);
                return new(new(LadderOutcome.Done, "Mount cannot park — tracking stopped. "),
                    stopped ? MountRest.TrackingOff : MountRest.Unknown);
            }
            return new(new(LadderOutcome.Skipped, string.Empty), MountRest.Unknown);
        } catch (Exception ex) {
            LogStepFailed("mount-rest", ex);
            return new(new(LadderOutcome.Failed, "Mount park/tracking-stop FAILED. "), MountRest.Unknown);
        }
    }

    /// <summary>Disconnect one ancillary device. The camera deliberately waits for the warm-up.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort by design: a fault in one step must not stop the rest of the shutdown.")]
    public async Task<LadderStep> DisconnectAsync(AncillaryDevice device) {
        var (name, disconnect) = device switch {
            AncillaryDevice.FilterWheel => ("Filter wheel", _filterWheel is null ? null : (Func<CancellationToken, Task<OperationAcceptedDto>>)(ct => _filterWheel.DisconnectAsync(null, ct))),
            AncillaryDevice.Focuser => ("Focuser", _focuser is null ? null : ct => _focuser.DisconnectAsync(null, ct)),
            AncillaryDevice.Rotator => ("Rotator", _rotator is null ? null : ct => _rotator.DisconnectAsync(null, ct)),
            _ => ("Flat panel", _flatDevice is null ? null : ct => _flatDevice.DisconnectAsync(null, ct)),
        };
        if (disconnect is null) return new(LadderOutcome.Skipped, string.Empty);
        try {
            await disconnect(CancellationToken.None).ConfigureAwait(false);
            return new(LadderOutcome.Done, $"{name} disconnected. ");
        } catch (Exception ex) {
            LogStepFailed($"disconnect-{name.ToLowerInvariant()}", ex);
            return new(LadderOutcome.Failed, $"{name} disconnect FAILED. ");
        }
    }

    /// <summary>
    /// §58.12 step "warm cooler": step the set-point from the current CCD
    /// temperature toward <see cref="WarmTargetC"/> at the profile's
    /// <c>CoolerRampCPerMin</c> (default 1 °C/min), then switch the cooler off.
    /// Bounded by <see cref="WarmHardCap"/>. A camera with the cooler already
    /// off (or no readable temperature) skips the ramp entirely. An abandon during the
    /// (long) ramp leaves the cooler ON at its current set-point and answers
    /// <see cref="LadderOutcome.Cancelled"/>; an exception answers <see cref="LadderOutcome.Failed"/>.
    /// In both cases the caller leaves the camera connected.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort by design: a fault in one step must not stop the rest of the shutdown.")]
    public async Task<LadderStep> WarmCoolerAsync(CancellationToken abandon) {
        if (_camera is null) return new(LadderOutcome.Skipped, string.Empty);
        try {
            return await WarmAsync(_camera, abandon).ConfigureAwait(false);
        } catch (Exception ex) {
            LogStepFailed("camera", ex);
            return new(LadderOutcome.Failed, "Camera warm-up/disconnect FAILED. ");
        }
    }

    /// <summary>Disconnect the camera — only AFTER the warm-up, per §58.12 (a disconnect mid-warm-up
    /// strands the TEC cold and the sensor warms violently when power drops).</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort by design: a fault in one step must not stop the rest of the shutdown.")]
    public async Task<LadderStep> DisconnectCameraAsync() {
        if (_camera is null) return new(LadderOutcome.Skipped, string.Empty);
        try {
            await _camera.DisconnectAsync(null, CancellationToken.None).ConfigureAwait(false);
            return new(LadderOutcome.Done, "Camera disconnected. ");
        } catch (Exception ex) {
            LogStepFailed("camera", ex);
            return new(LadderOutcome.Failed, "Camera warm-up/disconnect FAILED. ");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A ramp step that throws (interlock refusal, driver rejection) is logged and cuts the ramp short so the final cooler-off still runs; the profile read is best-effort.")]
    private async Task<LadderStep> WarmAsync(ICameraService camera, CancellationToken abandon) {
        var dto = await camera.GetAsync(CancellationToken.None).ConfigureAwait(false);
        var runtime = dto?.Runtime;
        if (runtime is not { CoolerOn: true }) return new(LadderOutcome.Skipped, string.Empty); // nothing to warm
        if (runtime.CcdTemperature is not double startTemp || startTemp >= WarmTargetC) {
            // No readable temperature to ramp from (or already warm): just cut
            // the cooler — with the TEC near ambient there's no thermal shock.
            await camera.SetCoolerAsync(false, null, CancellationToken.None).ConfigureAwait(false);
            return new(LadderOutcome.Done, "Cooler switched off. ");
        }

        double rampPerMin = 1.0;
        try {
            var ramp = _profiles?.GetImagingDefaults().CoolerRampCPerMin;
            if (ramp is > 0) rampPerMin = ramp.Value;
        } catch (Exception ex) {
            LogPolicyReadFailed(ex);
        }
        // One ramp-°C step per scaled minute: the slope the hardware sees is
        // exactly the profile's °C/min at scale 1; a test scale only shortens
        // the sleeps, never changes the per-step temperature delta.
        var stepC = rampPerMin;
        var stepSleep = TimeSpan.FromMinutes(1.0 / WarmTimeScale);
        var setpoint = startTemp;
        var started = DateTimeOffset.UtcNow;
        var stepFailed = false;
        while (setpoint < WarmTargetC && DateTimeOffset.UtcNow - started < WarmHardCap) {
            setpoint = Math.Min(WarmTargetC, setpoint + stepC);
            try {
                await camera.SetCoolerAsync(true, setpoint, CancellationToken.None).ConfigureAwait(false);
            } catch (Exception ex) {
                // A ramp step that the camera service refuses (the #1076 fan interlock: fan
                // write failed with the cooler state unreadable) or that the driver rejects must
                // never skip the final cooler-off below — that is the step that matters
                // overnight. Log it, stop stepping, and fall through to the cut-off.
                LogRampStepFailed(setpoint, ex);
                stepFailed = true;
                break;
            }
            if (setpoint >= WarmTargetC) break;
            try {
                await Task.Delay(stepSleep, abandon).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                // The caller abandoned mid-ramp: leave the cooler ON at the current
                // set-point (a returning user may resume imaging — cutting their cooling is
                // the race the abandon signal prevents) and keep the camera connected.
                return new(LadderOutcome.Cancelled, $"Cooler warm-up abandoned at {setpoint:F0}°C set-point — cooler left ON. ");
            }
        }
        await camera.SetCoolerAsync(false, null, CancellationToken.None).ConfigureAwait(false);
        // Be honest about WHICH condition ended the ramp: a hard-cap exit cut
        // the cooler off short of the target — the abrupt-ish cutoff the ramp
        // exists to soften — and the morning summary must not read like a
        // completed warm-up (the sensor may still have been cold at power cut).
        var text = stepFailed
            ? $"Cooler warm-up step to {setpoint:F0}°C FAILED — cooler switched off early. "
            : setpoint >= WarmTargetC
                ? $"Cooler warmed from {startTemp:F0}°C and switched off. "
                : $"Cooler warm-up HIT THE {WarmHardCap.TotalMinutes:F0}-minute cap at {setpoint:F0}°C set-point (target {WarmTargetC:F0}°C) — switched off early. ";
        return new(LadderOutcome.Done, text);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "SHUTDOWN_LADDER cooler warm-up step to {Setpoint}°C failed; cutting the cooler off early")]
    private partial void LogRampStepFailed(double setpoint, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SHUTDOWN_LADDER profile read failed — using the default ramp")]
    private partial void LogPolicyReadFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SHUTDOWN_LADDER step {Step} failed")]
    private partial void LogStepFailed(string step, Exception ex);
}