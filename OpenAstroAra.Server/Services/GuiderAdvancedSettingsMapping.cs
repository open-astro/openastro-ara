#region "copyright"
/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/
#endregion "copyright"

using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Server.Contracts;
using System.Collections.Generic;

namespace OpenAstroAra.Server.Services;

/// <summary>#1364 — the guider-advanced profile block ↔ the guider's settings record, and the write
/// boundary's validation. Pure.</summary>
public static class GuiderAdvancedSettingsMapping {

    /// <summary>The guider's limits for these settings (openastro-guider event_server.cpp / backlash_comp.cpp).</summary>
    public const int MaxPulseLimitMs = 1_000_000;
    public const int BacklashPulseMinMs = GuiderAdvancedLimits.BacklashPulseMinMs;
    public const int BacklashPulseMaxMs = GuiderAdvancedLimits.BacklashPulseMaxMs;

    /// <summary>The guider's noise-reduction names, the only values it accepts.</summary>
    public static readonly IReadOnlyList<string> NoiseReductionMethods = ["None", "2x2 Mean", "3x3 Median"];

    public static GuiderAdvancedSettings ToEngine(GuiderAdvancedSettingsDto d) => new() {
        // #1592 — on unless the profile explicitly says off (new and older profiles hold null).
        MultiStarEnabled = d.MultiStarEnabled ?? true,
        FastRecenter = d.FastRecenter,
        TolerateJumps = d.TolerateJumps,
        TolerateJumpsThresholdPx = d.TolerateJumpsThresholdPx,
        MinStarHfd = d.MinStarHfd,
        MaxStarHfd = d.MaxStarHfd,
        MinStarSnr = d.MinStarSnr,
        MassChangeThresholdEnabled = d.MassChangeThresholdEnabled,
        MassChangeThreshold = d.MassChangeThreshold,
        SearchRegionPx = d.SearchRegionPx,
        AutoExposureMinMs = d.AutoExposureMinMs,
        AutoExposureMaxMs = d.AutoExposureMaxMs,
        AutoExposureTargetSnr = d.AutoExposureTargetSnr,
        NoiseReduction = d.NoiseReduction,
        UseSubframes = d.UseSubframes,
        MaxRaDurationMs = d.MaxRaDurationMs,
        MaxDecDurationMs = d.MaxDecDurationMs,
        BacklashEnabled = d.BacklashEnabled,
        BacklashPulseMs = d.BacklashPulseMs,
        BacklashFloorMs = d.BacklashFloorMs,
        BacklashCeilingMs = d.BacklashCeilingMs,
        AssumeOrthogonal = d.AssumeOrthogonal,
        StopGuidingWhenSlewing = d.StopGuidingWhenSlewing,
        CalFlipRequiresDecFlip = d.CalFlipRequiresDecFlip,
        VariableDelayEnabled = d.VariableDelayEnabled,
        VariableDelayShortS = d.VariableDelayShortS,
        VariableDelayLongS = d.VariableDelayLongS,
        DitherScaleFactor = d.DitherScaleFactor,
        DitherRaOnly = d.DitherRaOnly,
        RaAlgorithm = d.RaAlgorithm,
        DecAlgorithm = d.DecAlgorithm,
    };

    public static GuiderAdvancedSettingsDto ToDto(GuiderAdvancedSettings a) => new(
        a.MultiStarEnabled, a.FastRecenter, a.TolerateJumps, a.TolerateJumpsThresholdPx,
        a.MinStarHfd, a.MaxStarHfd, a.MinStarSnr, a.MassChangeThresholdEnabled, a.MassChangeThreshold, a.SearchRegionPx,
        a.AutoExposureMinMs, a.AutoExposureMaxMs, a.AutoExposureTargetSnr,
        a.NoiseReduction, a.UseSubframes,
        a.MaxRaDurationMs, a.MaxDecDurationMs,
        a.BacklashEnabled, a.BacklashPulseMs, a.BacklashFloorMs, a.BacklashCeilingMs,
        a.AssumeOrthogonal, a.StopGuidingWhenSlewing, a.CalFlipRequiresDecFlip,
        a.VariableDelayEnabled, a.VariableDelayShortS, a.VariableDelayLongS,
        a.DitherScaleFactor, a.DitherRaOnly,
        a.RaAlgorithm, a.DecAlgorithm);

    /// <summary>The first thing wrong with a PUT body, or null. Set numbers without a range of their own must
    /// be positive (Ara treats "unset" as null, never 0); min ≤ max where both are set; the noise method is
    /// one of the guider's names; the variable delay comes as all three values or none (the guider needs them
    /// together); and every value meets <see cref="GuiderAdvancedLimits"/>, where a variable delay may be 0.</summary>
    public static string? Validate(GuiderAdvancedSettingsDto d) {
        static bool Bad(double? v) => v is double x && !(double.IsFinite(x) && x > 0);
        static bool BadInt(int? v) => v is int x && x <= 0;
        (string Name, double? Value)[] numbers = [
            ("min_star_snr", d.MinStarSnr), ("auto_exposure_target_snr", d.AutoExposureTargetSnr),
        ];
        foreach (var (name, value) in numbers) {
            if (Bad(value)) {
                return $"{name} must be a positive number";
            }
        }
        (string Name, int? Value)[] ints = [
            ("auto_exposure_min_ms", d.AutoExposureMinMs), ("auto_exposure_max_ms", d.AutoExposureMaxMs),
        ];
        foreach (var (name, value) in ints) {
            if (BadInt(value)) {
                return $"{name} must be a positive whole number";
            }
        }
        // The guider's own ranges (event_server.cpp set_guide_limits / set_backlash_comp): a max pulse of
        // 0 is a real value (no correction on that axis), up to 1,000,000 ms; the backlash pulse is 20–8000 ms.
        foreach (var (name, value) in new (string, int?)[] { ("max_ra_duration_ms", d.MaxRaDurationMs), ("max_dec_duration_ms", d.MaxDecDurationMs) }) {
            if (value is int ms && ms is < 0 or > MaxPulseLimitMs) {
                return $"{name} must be 0–{MaxPulseLimitMs} ms";
            }
        }
        if (d.AutoExposureMinMs is int minExp && d.AutoExposureMaxMs is int maxExp && minExp > maxExp) {
            return "auto_exposure_min_ms must not exceed auto_exposure_max_ms";
        }
        if (d.NoiseReduction is { } nr && !NoiseReductionMethods.Contains(nr)) {
            return $"noise_reduction must be one of: {string.Join(", ", NoiseReductionMethods)}";
        }
        var delaySet = (d.VariableDelayEnabled is not null ? 1 : 0) + (d.VariableDelayShortS is not null ? 1 : 0) + (d.VariableDelayLongS is not null ? 1 : 0);
        if (delaySet is 1 or 2) {
            return "variable_delay_enabled, variable_delay_short_s and variable_delay_long_s go together: set all three or none";
        }
        if (d.RaAlgorithm is { } ra && string.IsNullOrWhiteSpace(ra)) {
            return "ra_algorithm must be a name, or null to keep the guider's";
        }
        if (d.DecAlgorithm is { } dec && string.IsNullOrWhiteSpace(dec)) {
            return "dec_algorithm must be a name, or null to keep the guider's";
        }
        return GuiderAdvancedLimits.Apply(ToEngine(d)).Violations is [var first, ..] ? $"{first.Field} {first.Limit}" : null;
    }
}
