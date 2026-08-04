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
using OpenAstroAra.PlateSolving;
using OpenAstroAra.PlateSolving.Interfaces;
using OpenAstroAra.Profile.Interfaces;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §45 — solve one guider-saved polar-align FITS into an apparent-of-date pointing. The seam the
/// <see cref="PolarAlignService"/> loop calls per frame; faked in tests so the state machine is
/// exercised without ASTAP or files. A solve failure (no stars, clouds) is a NORMAL outcome
/// (<c>Success=false</c>), not an exception — the loop pauses and retries; configuration problems
/// (no guide optics, no solver path) throw <see cref="PlateSolverConfigurationException"/> so Start
/// can fail fast with an actionable message.
/// </summary>
public interface IPolarAlignFrameSolver {

    /// <summary>
    /// Solve the FITS at <paramref name="fitsPath"/>. <paramref name="hintRaDegJnow"/>/<paramref name="hintDecDegJnow"/>
    /// (apparent-of-date degrees), when supplied, seed a near solve around the mount's pointing —
    /// essential for the small guide-cam FOV. Returns apparent-of-date degrees (precessed from the
    /// solver's J2000 output — precession is arcmin-scale since J2000 and may not be skipped, §45.8).
    /// </summary>
    Task<PolarAlignSolveOutcome> SolveAsync(string fitsPath, double? hintRaDegJnow, double? hintDecDegJnow, CancellationToken ct);
}

/// <summary>One solved (or unsolved) §45 frame. Coordinates are apparent-of-date degrees, valid only
/// when <see cref="Success"/> is true.</summary>
public sealed record PolarAlignSolveOutcome(bool Success, double RaDegJnow, double DecDegJnow);

/// <summary>
/// §45 — the real frame solver: read the guider's 16-bit FITS, wrap it as <c>IImageData</c>, and run
/// the profile-configured solver (ASTAP) with the GUIDE optics — <c>Phd2SettingsDto.GuideFocalLength</c>
/// / <c>GuidePixelSize</c> (§63.19), not the main scope's, because the PA frames come from the guide
/// camera through the guider's <c>capture_single_frame</c>.
/// </summary>
public sealed class PolarAlignFrameSolver : IPolarAlignFrameSolver {

    private readonly IProfileService _profileService;
    private readonly IProfileStore _store;
    private readonly IPlateSolverFactory _solverFactory;

    public PolarAlignFrameSolver(IProfileService profileService, IProfileStore store, IPlateSolverFactory solverFactory) {
        _profileService = profileService;
        _store = store;
        _solverFactory = solverFactory;
    }

    public async Task<PolarAlignSolveOutcome> SolveAsync(
            string fitsPath, double? hintRaDegJnow, double? hintDecDegJnow, CancellationToken ct) {
        ArgumentException.ThrowIfNullOrWhiteSpace(fitsPath);

        // ARA store → legacy settings first (same rule and reason as PlateSolveService).
        LegacyProfileBridge.SyncPlateSolve(_profileService, _store);
        var profile = _profileService.ActiveProfile
            ?? throw new PlateSolverConfigurationException("Cannot polar-align: no active profile is loaded.");
        var settings = profile.PlateSolveSettings;

        // §63.19 guide optics — the PA frame is a guide-camera frame, so its pixel scale comes from
        // the guide focal length + guide pixel size. Fresh profiles leave both 0; fail fast with the
        // fix (they're set in Options → Guiding, or derived automatically for an OAG).
        var phd2 = _store.GetPhd2Settings();
        double focalLength = phd2.GuideFocalLength;
        double pixelSize = phd2.GuidePixelSize;
        if (!(focalLength > 0) || !(pixelSize > 0)) {
            throw new PlateSolverConfigurationException(
                $"Cannot polar-align: the guide focal length ({focalLength}) and guide-camera pixel size ({pixelSize}) " +
                "must both be configured (> 0) — set them in Options → Guiding (an OAG derives the focal length automatically).");
        }

        var image = LoadImageData(fitsPath);

        // Near-solver only, twice: the §45 loop must fail FAST on a cloudy/starless frame and retry
        // (~1 s cadence), so the long blind-solve fallback the imaging path uses is wrong here — the
        // hint (mount pointing / last solve) is always available to a running routine.
        var plateSolver = _solverFactory.GetPlateSolver(settings);
        var imageSolver = _solverFactory.GetImageSolver(plateSolver, plateSolver);

        Coordinates? hint = null;
        if (hintRaDegJnow is double ra && hintDecDegJnow is double dec) {
            hint = new Coordinates(ra, dec, Epoch.JNOW, Coordinates.RAType.Degrees).Transform(Epoch.J2000);
        }

        var parameter = new PlateSolveParameter {
            FocalLength = focalLength,
            PixelSize = pixelSize,
            SearchRadius = settings.SearchRadius,
            Regions = settings.Regions,
            DownSampleFactor = settings.DownSampleFactor,
            MaxObjects = settings.MaxObjects,
            Binning = 1, // the guider saves the frame at its native (already-binned) scale
            Coordinates = hint,
            // No blind fallback — it defaults to TRUE and would turn every cloudy frame into a
            // whole-sky solve that stalls the ~1s loop (and the lease-renew cadence) for its full
            // duration. The §45 loop's contract is fail-fast-and-retry; a failed near solve is one
            // failed iteration, not a cue to search the entire sky near the sparse pole field.
            BlindFailoverEnabled = false,
        };

        var result = await imageSolver.Solve(image, parameter, progress: null, ct).ConfigureAwait(false);
        if (result?.Success != true || result.Coordinates is null) {
            return new PolarAlignSolveOutcome(false, 0, 0);
        }
        var apparent = result.Coordinates.Transform(Epoch.JNOW);
        return new PolarAlignSolveOutcome(true, apparent.RADegrees, apparent.Dec);
    }

    // The guider writes 16-bit unsigned FITS (openastro-guider POLAR_ALIGNMENT_DESIGN.md §12.1 spike
    // gate); same wrap the frame repository uses for catalogued frames. Guide cams are mono in
    // practice and the solver ignores CFA anyway → isBayered false.
    private OpenAstroAra.Image.ImageData.BaseImageData LoadImageData(string fitsPath) {
        using var fits = OpenAstroAra.Fits.FitsImage.Open(fitsPath);
        var (w, h) = fits.GetDimensions();
        var pixels = fits.ReadImageData16();
        return new OpenAstroAra.Image.ImageData.BaseImageData(
            pixels, w, h, bitDepth: 16, isBayered: false,
            new OpenAstroAra.Image.ImageData.ImageMetaData(), _profileService, null!, null!);
    }
}
