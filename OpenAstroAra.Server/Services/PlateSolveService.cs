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
using OpenAstroAra.Image.Interfaces;
using OpenAstroAra.PlateSolving;
using OpenAstroAra.PlateSolving.Interfaces;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Core.Utility.Notification;
using OpenAstroAra.Server.Contracts;
using System.Globalization;
using OpenAstroAra.Profile.Interfaces;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §18.I — headless image plate-solving (see <see cref="IPlateSolveService"/>). Builds the configured solver
/// via <see cref="IPlateSolverFactory"/>, assembles a <see cref="PlateSolveParameter"/> from the active profile
/// (focal length, pixel size, search/downsample tuning), and returns the astrometric solution.
/// </summary>
public class PlateSolveService : IPlateSolveService {
    private readonly IProfileService profileService;
    private readonly IPlateSolverFactory plateSolverFactory;
    private readonly IProfileStore? profileStore;

    public PlateSolveService(IProfileService profileService, IPlateSolverFactory plateSolverFactory,
            IProfileStore? profileStore = null) {
        this.profileService = profileService;
        this.plateSolverFactory = plateSolverFactory;
        this.profileStore = profileStore;
    }

    /// <summary>The solve geometry, ARA profile first: the user configures optics in the ARA store's
    /// §36 section (Options → Imaging → Optics) — the Equipment layer's legacy profile never learns it
    /// (its focal length stays NaN), which used to fail every solve with an "unconfigured" message on a
    /// fully configured rig. Effective focal length folds the reducer in; legacy values remain the
    /// fallback for benches without a store. Static-testable via <see cref="ResolveSolveGeometry"/>.</summary>
    internal static (double FocalLength, double PixelSize) ResolveSolveGeometry(
            OpenAstroAra.Server.Contracts.OpticsSettingsDto? optics, double legacyFocalLength, double legacyPixelSize) {
        var focal = optics is { FocalLengthMm: > 0 }
            ? optics.FocalLengthMm * (optics.ReducerFactor > 0 ? optics.ReducerFactor : 1.0)
            : legacyFocalLength;
        var pixel = optics is { PixelSizeUm: > 0 } ? optics.PixelSizeUm : legacyPixelSize;
        return (focal, pixel);
    }

    public Task<PlateSolveResult> SolveImage(IImageData image, Coordinates? approxCoordinates, IProgress<ApplicationStatus>? progress, CancellationToken token) =>
        Solve(image, approxCoordinates, progress, learnOptics: false, token);

    public Task<PlateSolveResult> SolveImageAndLearnOptics(IImageData image, Coordinates? approxCoordinates, IProgress<ApplicationStatus>? progress, CancellationToken token) =>
        Solve(image, approxCoordinates, progress, learnOptics: true, token);

    private Task<PlateSolveResult> Solve(IImageData image, Coordinates? approxCoordinates, IProgress<ApplicationStatus>? progress, bool learnOptics, CancellationToken token) {
        ArgumentNullException.ThrowIfNull(image);

        // ARA store → legacy settings first: the solver stack below reads the legacy profile.
        LegacyProfileBridge.SyncPlateSolve(profileService, profileStore);
        var profile = profileService.ActiveProfile
            ?? throw new PlateSolverConfigurationException("Cannot plate-solve: no active profile is loaded.");
        var settings = profile.PlateSolveSettings;

        // Focal length + pixel size set the pixel scale / FOV the solver searches; a fresh profile leaves them
        // unset (focal length defaults to NaN), which yields a degenerate FOV and an opaque solver failure.
        // Fail fast with an actionable message instead. PlateSolverConfigurationException (a setup error the
        // user fixes) lets the API layer return a narrow 422 without catching unrelated InvalidOperationExceptions.
        var (focalLength, pixelSize) = ResolveSolveGeometry(
            profileStore?.GetOpticsSettings(), profile.TelescopeSettings.FocalLength, profile.CameraSettings.PixelSize);
        if (!(focalLength > 0) || !(pixelSize > 0)) {
            throw new PlateSolverConfigurationException(
                $"Cannot plate-solve: telescope focal length ({focalLength}) and camera pixel size ({pixelSize}) must both be configured (> 0) — set them in Options → Imaging → Optics.");
        }

        var plateSolver = plateSolverFactory.GetPlateSolver(settings);
        var blindSolver = plateSolverFactory.GetBlindSolver(settings);
        var imageSolver = plateSolverFactory.GetImageSolver(plateSolver, blindSolver);

        var parameter = new PlateSolveParameter {
            FocalLength = focalLength,
            PixelSize = pixelSize,
            SearchRadius = settings.SearchRadius,
            Regions = settings.Regions,
            DownSampleFactor = settings.DownSampleFactor,
            MaxObjects = settings.MaxObjects,
            // The frame's own binning when its metadata carries one (a binned capture solves at pixel
            // size × bin), else native scale.
            Binning = BinningOf(image),
            Coordinates = approxCoordinates,
            Timeout = TimeoutFrom(settings),
        };

        return learnOptics
            ? SolveAndLearnScale(imageSolver, image, parameter, progress, token)
            : imageSolver.Solve(image, parameter, progress, token);
    }

    private async Task<PlateSolveResult> SolveAndLearnScale(IImageSolver imageSolver, IImageData image, PlateSolveParameter parameter,
            IProgress<ApplicationStatus>? progress, CancellationToken token) {
        var result = await imageSolver.Solve(image, parameter, progress, token).ConfigureAwait(false);
        if (result?.Success == true) {
            LearnFocalLength(parameter, result);
        }
        return result!;
    }

    /// <summary>#1136 — a manual solve that lands on a pixel scale more than
    /// <see cref="SolveTuning.ScaleMismatchTolerance"/> away from the profile's means the optics are
    /// wrong (a reducer or a typo); every hinted solve after it would start from a wrong field of view.
    /// Write the sky-measured focal length back to the profile (reducer factored out, so the user's
    /// reducer entry stays) and say so. Reached only through <see cref="SolveImageAndLearnOptics"/>
    /// (the REST solve the user triggers); <see cref="SolveImage"/>, which the center-after-drift feed
    /// calls mid-sequence, and the centering/rotation loops never get here.</summary>
    internal void LearnFocalLength(PlateSolveParameter parameter, PlateSolveResult result) {
        if (profileStore is null) {
            return;
        }
        var expected = AstroUtil.ArcsecPerPixel(parameter.PixelSize, parameter.FocalLength);
        if (!SolveTuning.ScaleMismatch(expected, result.Pixscale)) {
            return;
        }
        var measuredEffective = SolveTuning.FocalLengthFromScale(parameter.PixelSize, result.Pixscale);
        if (double.IsNaN(measuredEffective)) {
            return;
        }
        OpticsSettingsDto? before = null;
        var after = profileStore.UpdateOpticsSettings(optics => {
            before = optics;
            var reducer = optics.ReducerFactor > 0 ? optics.ReducerFactor : 1.0;
            return optics with { FocalLengthMm = Math.Round(measuredEffective / reducer, 1) };
        });
        if (before is null || after is null) {
            return;
        }
        Logger.Warning($"Plate solve - profile focal length {before.FocalLengthMm:0.#} mm updated to the sky-measured {after.FocalLengthMm:0.#} mm (solved scale {result.Pixscale:0.00}\"/px, expected {expected:0.00}\"/px). Check Options → Imaging → Optics.");
        Notifier.ShowWarning(string.Format(CultureInfo.InvariantCulture,
            "Plate solve measured a focal length of {0:0} mm; the profile said {1:0} mm. Optics updated — check Options → Imaging → Optics.",
            after.FocalLengthMm, before.FocalLengthMm));
    }

    internal static int BinningOf(IImageData image) {
        var bin = image.MetaData?.Camera?.BinX ?? 1;
        return bin > 0 ? bin : 1;
    }

    /// <summary>The per-solve budget from the profile's timeout, or null (the solver's legacy cap)
    /// when the profile has none; shared by every PlateSolveParameter builder.</summary>
    public static TimeSpan? TimeoutFrom(IPlateSolveSettings settings) =>
        settings.SolveTimeoutSeconds > 0 ? TimeSpan.FromSeconds(settings.SolveTimeoutSeconds) : null;
}
