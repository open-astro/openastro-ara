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

using OpenAstroAra.Server.Contracts;

namespace OpenAstroAra.Server.Services;

/// <summary>Validation + assembly of a per-request Tonight's Sky optics override (§36.8 slice 4a). Kept as
/// a standalone static validator (not a method on <see cref="TonightSkyService"/>) so the endpoint's input
/// validation doesn't couple to the concrete service implementation — swapping or mocking
/// <see cref="ITonightSkyService"/> leaves this guard intact.</summary>
public static class TonightSkyOverrides {
    // Per-request reducer override ceiling. A focal reducer shrinks the effective focal length (≈0.5×),
    // a barlow extends it (up to ≈5×); 10× is a generous physical cap. Bounding it matters because the
    // reducer multiplies the focal length — an absurd value (typo/fuzz) collapses the pixel-scale FOV to
    // sub-arcsecond and frames every object TooSmall/Unknown, a useless 200 with no diagnostic.
    public const double MaxReducerFactor = 10.0;

    /// <summary>Validate + assemble a per-request optics override. Each <em>supplied</em> field must be
    /// finite and &gt; 0 (the reducer additionally ≤ <see cref="MaxReducerFactor"/>); un-supplied fields are
    /// merged from the active profile, fetched lazily via <paramref name="getProfileOptics"/> so a request
    /// that supplies all five fields never reads the profile at all. The fully-assembled train is then
    /// re-checked so a field merged onto an un/partly-configured profile (zero focal/sensor/pixel) is
    /// rejected rather than silently yielding a NaN FOV → all-<see cref="FramingFit.Unknown"/> 200. Returns
    /// the merged override on success, or a human-readable 400 message in <c>Error</c> (then <c>Override</c>
    /// is null). Callers invoke this only when at least one field is supplied, so a success always carries a
    /// non-null DTO.
    /// <para>NB the per-field guards use <see cref="double.IsFinite(double)"/>, not a bare <c>&gt; 0</c>:
    /// <c>NaN &lt;= 0</c> and <c>+∞ &lt;= 0</c> are both <c>false</c> in C#, so a relational guard alone
    /// lets NaN/∞ through into <see cref="TonightSkyService.FovArcmin"/>.</para></summary>
    public static (OpticsSettingsDto? Override, string? Error) Build(
            Func<OpticsSettingsDto> getProfileOptics, double? focalLengthMm, double? reducer,
            int? sensorW, int? sensorH, double? pixelUm) {
        if (focalLengthMm is { } fl && !(double.IsFinite(fl) && fl > 0)) {
            return (null, "Query parameter 'focalLengthMm' must be a finite value > 0.");
        }
        if (reducer is { } r && !(double.IsFinite(r) && r > 0 && r <= MaxReducerFactor)) {
            return (null, $"Query parameter 'reducer' must be a finite value in (0, {MaxReducerFactor}].");
        }
        if (pixelUm is { } pu && !(double.IsFinite(pu) && pu > 0)) {
            return (null, "Query parameter 'pixelUm' must be a finite value > 0.");
        }
        if (sensorW is { } sw && sw <= 0) {
            return (null, "Query parameter 'sensorW' must be > 0.");
        }
        if (sensorH is { } sh && sh <= 0) {
            return (null, "Query parameter 'sensorH' must be > 0.");
        }

        // Read the active profile only when a field is left to fall back on — if all five are supplied the
        // override is fully specified and the profile read is wasted.
        OpticsSettingsDto merged;
        if (focalLengthMm is { } f && reducer is { } rf && sensorW is { } w
                && sensorH is { } h && pixelUm is { } p) {
            merged = new OpticsSettingsDto(f, rf, w, h, p);   // fully specified — no profile read
        } else {
            var profile = getProfileOptics();
            merged = new OpticsSettingsDto(
                focalLengthMm ?? profile.FocalLengthMm,
                reducer ?? profile.ReducerFactor,
                sensorW ?? profile.SensorWidthPx,
                sensorH ?? profile.SensorHeightPx,
                pixelUm ?? profile.PixelSizeUm);
        }

        // Geometry fields (focal/pixel/sensor) must be finite-positive whether supplied or merged — a field
        // left to a zero/unset profile yields a NaN FOV → all-Unknown silent 200.
        if (!(double.IsFinite(merged.FocalLengthMm) && merged.FocalLengthMm > 0
              && double.IsFinite(merged.PixelSizeUm) && merged.PixelSizeUm > 0
              && merged.SensorWidthPx > 0 && merged.SensorHeightPx > 0)) {
            return (null, "The assembled optics are incomplete — supply the fields your active profile does "
                + "not already have set (focalLengthMm, sensorW, sensorH, pixelUm; reducer is optional only "
                + "when your profile already has a valid one).");
        }

        // The reducer cap is re-applied to the ASSEMBLED value, not just a supplied one: a profile carrying
        // an out-of-range ReducerFactor (direct edit / pre-cap migration / unset 0) merged in when the
        // caller omits 'reducer' would otherwise pass with no error and collapse/NaN the FOV silently.
        if (!(double.IsFinite(merged.ReducerFactor) && merged.ReducerFactor > 0
              && merged.ReducerFactor <= MaxReducerFactor)) {
            return (null, $"The assembled reducer factor ({merged.ReducerFactor}) is out of range — supply "
                + $"'reducer' in (0, {MaxReducerFactor}] (your active profile's reducer is unset or invalid).");
        }

        return (merged, null);
    }
}
