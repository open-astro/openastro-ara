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
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.PlateSolving;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.Refusals;
using OpenAstroAra.Server.Endpoints;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>#2173 — which camera the plate-solve check would use now: <see cref="PoseSolveCameras.Main"/>, or
/// <see cref="PoseSolveCameras.Guide"/> only when there is no main camera; null with <paramref name="UnavailableReason"/>
/// (a <see cref="PoseSolveAvailabilityDto.UnavailableReason"/> token) when none can shoot now. <paramref name="Key"/> names
/// the camera and its rotator angle for the stored position angle.</summary>
public sealed record PoseSolveCameraChoice(string? Camera, string? Key, string? UnavailableReason);

/// <summary>#2173 — one solved (or unsolved) frame of the plate-solve check. <paramref name="Coordinates"/> carries its own
/// epoch; <paramref name="PixelScaleArcsec"/> the frame's scale (the solve's uncertainty is a few of its pixels).</summary>
public sealed record PoseSolveFrame(bool Success, Coordinates? Coordinates, double? PositionAngleDeg, double? PixelScaleArcsec,
    string? Failure = null);

public static class PoseSolveCameras {
    public const string Main = "main";
    public const string Guide = "guide";
}

/// <summary>#2173 — the camera half of the plate-solve check: one short exposure through the existing solve pipelines
/// (ADR 0007: ASTAP), blind if the near solve misses. Faked in tests. A busy camera, no camera, or a solver or optics that
/// is not set up throws the declared <see cref="RefusalException"/>; a frame that does not solve is
/// <see cref="PoseSolveFrame.Success"/> false. <c>exposed</c> runs once, synchronously, when the frame is read out and
/// before it is solved (the check reads the mount then, not after a solve that may take minutes); a capture that ends
/// with no frame may not run it.</summary>
public interface IPoseSolveCamera {
    Task<PoseSolveCameraChoice> ChooseAsync(CancellationToken ct);

    Task<PoseSolveFrame> CaptureAndSolveAsync(PoseSolveCameraChoice camera, Coordinates? hint, Action exposed, CancellationToken ct);
}

/// <summary>The daemon's cameras: the main camera through <see cref="MainCameraPointingSolver"/> (the pipeline §57.4
/// Verify Position and the coarse Polar Align stage use), or, with no main camera, one guide frame through the guider
/// (ADR 0016 custody, as Polar Align's frames) solved by <see cref="IPolarAlignFrameSolver"/> with the guide optics.</summary>
internal sealed class PoseSolveCamera : IPoseSolveCamera {

    /// <summary>The check's exposure: as short as §57.4 Verify Position's.</summary>
    public const double ExposureSeconds = VerifyPositionService.DefaultExposureSeconds;

    private const string Purpose = "Cannot check the mount's position";

    private readonly MainCameraPointingSolver _main;
    private readonly ICameraService? _camera;
    private readonly IGuideCapture? _guider;
    private readonly IPolarAlignFrameSolver _guideSolver;
    private readonly IPolarAlignFrameFetcher _fetcher;
    private readonly IProfileStore _store;
    private readonly Func<IRotatorMediator?> _rotator;

    public PoseSolveCamera(MainCameraPointingSolver main, ICameraService? camera, IGuideCapture? guider, IPolarAlignFrameSolver guideSolver,
            IPolarAlignFrameFetcher fetcher, IProfileStore store, Func<IRotatorMediator?> rotator) {
        _main = main;
        _camera = camera;
        _guider = guider;
        _guideSolver = guideSolver;
        _fetcher = fetcher;
        _store = store;
        _rotator = rotator;
    }

    public async Task<PoseSolveCameraChoice> ChooseAsync(CancellationToken ct) {
        // ADR 0007: ASTAP is the solver; without its binary or a star database no frame can solve.
        if (!(SolverReady ?? AstapReady)()) {
            return new(null, null, "solver_not_set");
        }
        var camera = _camera is null ? null : await _camera.GetAsync(ct).ConfigureAwait(false);
        if (camera?.State == EquipmentConnectionState.Connected) {
            if (_main.MissingOptics().Length > 0) {
                return new(PoseSolveCameras.Main, null, "optics_not_set");
            }
            if (!_camera!.IsFreeToCapture(_main)) {
                return new(PoseSolveCameras.Main, null, "camera_busy");
            }
            return new(PoseSolveCameras.Main, MainKey(camera.DeviceId), null);
        }
        // Only with no main camera: the guide camera, through the guider.
        if (_guider is null || GuiderLinkOrNull() is null || _guider.OutOfStepReason is not null) {
            return new(null, null, "no_camera");
        }
        if (GuideCameraCustody.IsBusy(_guider.AppState) || _guider.CameraHolder is not null) {
            return new(PoseSolveCameras.Guide, null, "camera_busy");
        }
        return new(PoseSolveCameras.Guide, GuideKey(), null);
    }

    // Test knob: whether ASTAP and a star database are installed (the default reads the profile's solver settings).
    internal Func<bool>? SolverReady { get; init; }

    private bool AstapReady() {
        var solver = PlateSolveEndpoints.BuildDatabaseStatus(_store.GetPlateSolveSettings(), File.Exists);
        return solver.SolverFound && solver.FileCount > 0;
    }

    // The main camera with the rotator's mechanical angle (to the degree): a camera turned by the rotator keeps no
    // position angle from before.
    private string MainKey(string cameraId) => $"{PoseSolveCameras.Main}:{cameraId}:rotator {RotatorAngle()}";

    // The guide camera by its identity and guider profile, with the rotator's angle: an off-axis guider turns with the
    // rotator, so a stored angle from another rotator angle (or another guide camera) says nothing. Null when the profile
    // names no guide camera: then no angle is stored or compared.
    private string? GuideKey() {
        var guider = _store.GetPhd2Settings();
        if (guider is null) {
            return null;
        }
        var camera = !string.IsNullOrEmpty(guider.GuiderCameraAlpacaUniqueId) ? guider.GuiderCameraAlpacaUniqueId
            : !string.IsNullOrEmpty(guider.GuiderCameraId) ? guider.GuiderCameraId
            : guider.GuiderCamera;
        if (string.IsNullOrEmpty(camera)) {
            return null;
        }
        var profile = guider.Phd2ProfileId is int id ? id.ToString(CultureInfo.InvariantCulture) : guider.Phd2Profile;
        return $"{PoseSolveCameras.Guide}:{camera}:profile {profile}:rotator {RotatorAngle()}";
    }

    private string RotatorAngle() {
        var rotator = _rotator()?.GetInfo();
        return rotator is { Connected: true } r && float.IsFinite(r.MechanicalPosition)
            ? Math.Round(r.MechanicalPosition).ToString(CultureInfo.InvariantCulture)
            : "none";
    }

    private IGuideCaptureLink? GuiderLinkOrNull() {
        try {
            return _guider!.ConnectedLink();
        } catch (InvalidOperationException) {
            return null;
        }
    }

    public Task<PoseSolveFrame> CaptureAndSolveAsync(PoseSolveCameraChoice camera, Coordinates? hint, Action exposed, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(exposed);
        return camera.Camera == PoseSolveCameras.Guide ? GuideAsync(hint, exposed, ct) : MainAsync(hint, exposed, ct);
    }

    private async Task<PoseSolveFrame> MainAsync(Coordinates? hint, Action exposed, CancellationToken ct) {
        PlateSolveResult result;
        try {
            result = await _main.CaptureAndSolveRawAsync(ExposureSeconds, hint, blindFailover: true, Purpose, ct, exposed).ConfigureAwait(false);
        } catch (PlateSolverConfigurationException ex) {
            throw _main.MissingOptics() is { Length: > 0 } missing
                ? PlateSolveService.OpticsNotSet(missing, ex.Message)
                : PlateSolveService.SolverNotSet(ex);
        } catch (CameraUnavailableException ex) {
            throw new RefusalException(RefusalCatalog.NoCamera, ex.Message);
        } catch (StoreUnavailableException ex) {
            throw new RefusalException(RefusalCatalog.StorageUnavailable, ex.Message);
        } catch (ArgumentOutOfRangeException ex) {
            throw new RefusalException(RefusalCatalog.CaptureSettingsUnsupported, ex.Message);
        } catch (TimeoutException) {
            throw new RefusalException(RefusalCatalog.GateStuck, "The camera's in-flight gate is stuck — check the daemon log; a restart clears it.");
        }
        if (result?.Success != true || result.Coordinates is null) {
            return new(false, null, null, null, "the frame did not solve (clouds, a closed cover, or too few stars)");
        }
        return new(true, result.Coordinates, PolarAlignSolveOutcome.PositionAngleOf(result),
            double.IsFinite(result.Pixscale) && result.Pixscale > 0 ? result.Pixscale : null);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "One guide frame: a capture, fetch or solve fault is a failed check with its reason, never a daemon fault; configuration errors are translated into refusals first.")]
    private async Task<PoseSolveFrame> GuideAsync(Coordinates? hint, Action exposed, CancellationToken ct) {
        var link = GuiderLinkOrNull() ?? throw new RefusalException(RefusalCatalog.NoCamera, "No main camera is connected, and no guider is connected for the guide camera.");
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(ct);
        GuideCameraCustody.Claim claim;
        try {
            // Never take the camera from a running activity: the check is not worth stopping guiding or a polar alignment.
            // The custody module refuses, in one step, while the Guider is busy or anyone holds the camera; guiding (or a
            // newer activity) taking it later gives up the check.
            claim = await _guider!.TakeCameraIfFreeAsync(CameraUse.PoseCheck, _ => stopped.CancelAsync(), ct).ConfigureAwait(false);
        } catch (InvalidOperationException ex) {
            throw new RefusalException(RefusalCatalog.CameraBusy,
                "The guide camera is busy (guiding, polar alignment, focusing or the live view) — try again once it is free: " + ex.Message);
        }
        var workDir = Path.Combine(Path.GetTempPath(), "ara-pose-check-" + Guid.NewGuid().ToString("N"));
        try {
            Directory.CreateDirectory(workDir);
            await link.WaitForCaptureIdleAsync(stopped.Token).ConfigureAwait(false);
            var done = new TaskCompletionSource<SingleFrameCompleteEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            // Only a completion raised once this capture is asked for is its own: an earlier capture's late one (raised
            // before) would solve an old frame of another pointing.
            var asked = 0;
            using var subscription = link.OnFrameComplete(e => {
                if (Volatile.Read(ref asked) == 1) {
                    done.TrySetResult(e);
                }
            });
            // As Polar Align does: the profile's Polar Align binning, or one picked from the guide camera's frame size;
            // the capture and the solver are told the same value (a binned frame searched at 1×1 never solves).
            var frameSize = await ReadFrameSizeAsync(link, stopped.Token).ConfigureAwait(false);
            var binning = PolarAlignBinning.Resolve(_store.GetPolarAlignSettings()?.Binning ?? PolarAlignBinning.Auto, frameSize);
            Volatile.Write(ref asked, 1);
            await link.CaptureFrameAsync((int)Math.Round(ExposureSeconds * 1000.0), binning > 1 ? binning : null, stopped.Token).ConfigureAwait(false);
            var completed = await done.Task.WaitAsync(TimeSpan.FromSeconds(ExposureSeconds + 30), stopped.Token).ConfigureAwait(false);
            var filename = Path.GetFileName(completed.Filename ?? completed.Path ?? "");
            if (!completed.Success || string.IsNullOrEmpty(filename)) {
                return new(false, null, null, null, completed.Error ?? "the guider saved no frame");
            }
            exposed();
            if (string.IsNullOrEmpty(link.Host)) {
                return new(false, null, null, null, "the guider's address is unknown, so its frame cannot be fetched");
            }
            var localPath = Path.Combine(workDir, filename);
            await _fetcher.FetchAsync(link.Host, link.RpcPort, filename, localPath, stopped.Token).ConfigureAwait(false);
            var hintJnow = hint?.Transform(Epoch.JNOW);
            var outcome = await _guideSolver.SolveAsync(localPath, binning, hintJnow?.RADegrees, hintJnow?.Dec, blindFailover: true, stopped.Token)
                .ConfigureAwait(false);
            return outcome.Success
                ? new(true, new Coordinates(outcome.RaDegJnow, outcome.DecDegJnow, Epoch.JNOW, Coordinates.RAType.Degrees),
                    outcome.PositionAngleDeg, outcome.PixelScaleArcsec)
                : new(false, null, null, null, "the guide frame did not solve (clouds, a closed cover, or too few stars)");
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (PlateSolverConfigurationException ex) {
            throw new RefusalException(RefusalCatalog.SolverNotSet, ex.Message);
        } catch (Exception ex) {
            return new(false, null, null, null, "the guide frame failed: " + ex.Message);
        } finally {
            await claim.ReleaseAsync().ConfigureAwait(false);
            try { Directory.Delete(workDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // The guide camera's frame size, or null when the guider gives none (binning then falls back to 1×1).
    private static async Task<(int Width, int Height)?> ReadFrameSizeAsync(IGuideCaptureLink link, CancellationToken ct) {
        try {
            return await link.GetCameraFrameSizeAsync(ct).ConfigureAwait(false);
        } catch (InvalidOperationException) {
            return null;
        }
    }
}
