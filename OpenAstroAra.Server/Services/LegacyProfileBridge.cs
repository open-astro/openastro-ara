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
using OpenAstroAra.Profile.Interfaces;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// The dual-profile-systems bridge: the user configures everything in the ARA profile store
/// (<see cref="IProfileStore"/> — Options → Imaging → Plate Solving / Optics), but the NINA-forked
/// solver stack reads the Equipment layer's LEGACY <see cref="IProfileService"/> profile, which
/// nothing ever writes on the headless server (ASTAPLocation stays "", focal length stays NaN —
/// every solve failed "unconfigured" on a fully configured rig). This one-way sync copies the ARA
/// values onto the legacy live settings; every solver entry point (solve-frame, centering, polar
/// align) calls it first so all consumers see one truth. Mutating the legacy singleton is safe
/// here: on the headless server it is otherwise dead state with no other writer.
/// </summary>
public static class LegacyProfileBridge {

    // Serializes the sync: three entry points (solve-frame / centering / polar align) can run
    // concurrently and all write the same legacy profile object. The writes are idempotent
    // snapshots of the ARA store, so interleaving is harmless TODAY — the lock exists so a future
    // field with side effects (or a torn read-modify-write) can't reintroduce a real race.
    private static readonly object SyncGate = new();

    /// <summary>Copy the ARA store's plate-solve + optics sections onto the legacy profile's
    /// settings. No-op when either side is missing (benches without a store).</summary>
    public static void SyncPlateSolve(IProfileService? legacy, IProfileStore? store) {
        var profile = legacy?.ActiveProfile;
        if (profile is null || store is null) {
            return;
        }
        lock (SyncGate) {
            SyncLocked(profile, store);
        }
    }

    private static void SyncLocked(IProfile profile, IProfileStore store) {

        var ps = store.GetPlateSolveSettings();
        var s = profile.PlateSolveSettings;
        if (!string.IsNullOrWhiteSpace(ps.PathOrEndpoint)) {
            s.ASTAPLocation = ps.PathOrEndpoint;
        }
        // "astap" is the only engine the port ships; anything unknown keeps the legacy value
        // rather than silently switching solver families.
        if (string.Equals(ps.Engine, "astap", System.StringComparison.OrdinalIgnoreCase)) {
            s.PlateSolverType = PlateSolver.ASTAP;
            s.BlindSolverType = BlindSolver.ASTAP;
        }
        if (ps.SearchRadiusDeg > 0) {
            s.SearchRadius = ps.SearchRadiusDeg;
        }
        if (ps.DownsampleFactor > 0) {
            s.DownSampleFactor = ps.DownsampleFactor;
        }
        s.BlindFailoverEnabled = ps.UseBlindFallback;
        s.Sync = ps.SyncToCoordinates;

        // Optics → the legacy focal length / pixel size (belt to ResolveSolveGeometry's braces:
        // consumers that still read the legacy fields directly get real values too).
        var optics = store.GetOpticsSettings();
        if (optics.FocalLengthMm > 0) {
            profile.TelescopeSettings.FocalLength =
                optics.FocalLengthMm * (optics.ReducerFactor > 0 ? optics.ReducerFactor : 1.0);
        }
        if (optics.PixelSizeUm > 0) {
            profile.CameraSettings.PixelSize = optics.PixelSizeUm;
        }
    }
}
