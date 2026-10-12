#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Server.Contracts;
using System;
using System.Globalization;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §58.4 / §58.6 (#1138) — the pure decisions behind the meridian-flip recovery policies the profile
/// carries: whether to re-focus after the flip (<c>meridian_refocus</c>: always / if_temp_drifted / never,
/// against <c>meridian_refocus_temp_threshold_c</c>) and how to bring the guider back
/// (<c>meridian_guider_recal</c>: auto_restore / full / skip).
/// </summary>
public static class MeridianFlipPolicy {

    public const string RefocusAlways = "always";
    public const string RefocusIfTempDrifted = "if_temp_drifted";
    public const string RefocusNever = "never";

    public const string GuiderAutoRestore = "auto_restore";
    public const string GuiderFull = "full";
    public const string GuiderSkip = "skip";

    /// <summary>The refocus mode, normalised; an unknown string reads as the spec default.</summary>
    public static string RefocusMode(string? mode) => mode?.Trim().ToLowerInvariant() switch {
        RefocusAlways => RefocusAlways,
        RefocusNever => RefocusNever,
        _ => RefocusIfTempDrifted,
    };

    /// <summary>The guider mode, normalised; an unknown string reads as the spec default.</summary>
    public static string GuiderMode(string? mode) => mode?.Trim().ToLowerInvariant() switch {
        GuiderFull => GuiderFull,
        GuiderSkip => GuiderSkip,
        _ => GuiderAutoRestore,
    };

    /// <summary>
    /// Whether the flip recovery re-focuses, and why. <paramref name="lastAutofocusTemperatureC"/> is the
    /// focuser temperature at the session's last completed autofocus (null = none this session);
    /// <paramref name="focuserTemperatureC"/> the focuser's reading now (NaN/null = no probe).
    /// <c>if_temp_drifted</c>: re-focus when the drift exceeds the threshold, or when there has been no
    /// autofocus this session at all (nothing to compare against — hours of imaging have passed); a
    /// focuser without a temperature probe cannot report drift, so it keeps the focus it has.
    /// </summary>
    public static (bool Refocus, string Reason) RefocusDecision(string? mode, double thresholdC, double? lastAutofocusTemperatureC, double? focuserTemperatureC) {
        switch (RefocusMode(mode)) {
            case RefocusAlways:
                return (true, "policy: always re-focus after a flip");
            case RefocusNever:
                return (false, "policy: never re-focus after a flip");
        }
        if (lastAutofocusTemperatureC is not double last || !double.IsFinite(last)) {
            return (true, "no autofocus this session to compare against");
        }
        if (focuserTemperatureC is not double now || !double.IsFinite(now)) {
            return (false, "the focuser reports no temperature, so drift cannot be judged");
        }
        var drift = Math.Abs(now - last);
        var threshold = thresholdC > 0 ? thresholdC : 2.0;
        return drift > threshold
            ? (true, string.Create(CultureInfo.InvariantCulture, $"focuser temperature drifted {drift:0.#} °C since the last autofocus (threshold {threshold:0.#} °C)"))
            : (false, string.Create(CultureInfo.InvariantCulture, $"focuser temperature drifted only {drift:0.#} °C since the last autofocus (threshold {threshold:0.#} °C)"));
    }
}
