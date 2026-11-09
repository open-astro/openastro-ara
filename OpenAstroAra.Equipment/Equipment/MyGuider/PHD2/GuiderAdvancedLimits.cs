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
using System.Linq;

namespace OpenAstroAra.Equipment.Equipment.MyGuider.PHD2 {

    /// <summary>One setting the latest push did not land: the name the imager sees and why (#1748).</summary>
    public sealed record GuiderSettingNotApplied(string Setting, string Reason);

    /// <summary><paramref name="Field"/> is the wire name, <paramref name="Setting"/> the name the imager
    /// sees and <paramref name="Limit"/> the rule, without a subject.</summary>
    public sealed record GuiderLimitViolation(string Field, string Setting, string Limit) {
        /// <summary>What the imager reads beside <see cref="Setting"/>.</summary>
        public string Reason => $"{char.ToUpperInvariant(Limit[0])}{Limit[1..]}. Not sent.";
    }

    /// <summary>
    /// #1748 — the Guider's limits for the advanced settings, in one place: the write boundary refuses a value
    /// outside them and the push leaves one out. Here, not in the server's mapping, because the push is in this
    /// assembly. Pure.
    /// </summary>
    public static class GuiderAdvancedLimits {

        public const int BacklashPulseMinMs = 20;
        public const int BacklashPulseMaxMs = 8000;

        public static readonly IReadOnlyList<string> DecAlgorithms = ["Hysteresis", "Lowpass", "Lowpass2", "Resist Switch", "ZFilter"];
        public static readonly IReadOnlyList<string> RaAlgorithms = [.. DecAlgorithms, "Predictive PEC"];

        private sealed record Rule(string Field, string Setting, string Limit,
            Func<GuiderAdvancedSettings, bool> Bad, Func<GuiderAdvancedSettings, GuiderAdvancedSettings> Clear);

        private static bool OutOfRange(double? value, double max) => value is double v && !(double.IsFinite(v) && v > 0 && v <= max);

        private static bool BadDelay(double? value, double max) => value is double v && !(v >= 0 && v <= max && v == Math.Floor(v));

        private static bool Unknown(string? name, IReadOnlyList<string> known) =>
            !string.IsNullOrWhiteSpace(name) && !known.Contains(name.Trim());

        // The three only mean something together, so the group goes whole.
        private static GuiderAdvancedSettings NoBacklash(GuiderAdvancedSettings a) =>
            a with { BacklashEnabled = null, BacklashPulseMs = null, BacklashFloorMs = null, BacklashCeilingMs = null };

        private static GuiderAdvancedSettings NoVariableDelay(GuiderAdvancedSettings a) =>
            a with { VariableDelayEnabled = null, VariableDelayShortS = null, VariableDelayLongS = null };

        private static readonly Rule[] Rules = [
            new("search_region_px", "Search region", "must be a whole number from 7 to 50",
                a => a.SearchRegionPx is < 7 or > 50, a => a with { SearchRegionPx = null }),
            new("mass_change_threshold", "Mass change threshold", "must be above 0 and at most 1 (100%)",
                a => OutOfRange(a.MassChangeThreshold, 1), a => a with { MassChangeThreshold = null }),
            new("min_star_hfd", "Minimum star HFD", "must be a positive number",
                a => OutOfRange(a.MinStarHfd, double.MaxValue), a => a with { MinStarHfd = null }),
            new("max_star_hfd", "Maximum star HFD", "must be a positive number",
                a => OutOfRange(a.MaxStarHfd, double.MaxValue), a => a with { MaxStarHfd = null }),
            new("min_star_hfd", "Minimum star HFD", "must be below the maximum star HFD",
                a => a.MinStarHfd is double min && a.MaxStarHfd is double max && min >= max,
                a => a with { MinStarHfd = null, MaxStarHfd = null }),
            new("tolerate_jumps_threshold_px", "Jump threshold", "must be above 0 and at most 100",
                a => OutOfRange(a.TolerateJumpsThresholdPx, 100), a => a with { TolerateJumpsThresholdPx = null }),
            new("dither_scale_factor", "Dither scale", "must be above 0 and at most 100",
                a => OutOfRange(a.DitherScaleFactor, 100), a => a with { DitherScaleFactor = null }),
            new("backlash_pulse_ms", "Backlash pulse", $"must be {BacklashPulseMinMs}–{BacklashPulseMaxMs} ms",
                a => a.BacklashPulseMs is < BacklashPulseMinMs or > BacklashPulseMaxMs, NoBacklash),
            // The guider takes the enable only with a pulse width: an enable alone made it range-check the
            // pulse it already held (0) against its floor (20) and refuse the whole group on every push
            // (2026-10-18, "Floor must be in [20, 0 (PulseWidth)]").
            new("backlash_pulse_ms", "Backlash pulse", $"is required ({BacklashPulseMinMs}–{BacklashPulseMaxMs} ms) to turn backlash compensation on",
                a => a.BacklashEnabled == true && a.BacklashPulseMs is null, NoBacklash),
            new("backlash_floor_ms", "Backlash floor", $"must be at least {BacklashPulseMinMs} and at most the pulse width",
                a => a.BacklashFloorMs is int floor && (floor < BacklashPulseMinMs || (a.BacklashPulseMs is int pulse && floor > pulse)), NoBacklash),
            new("backlash_ceiling_ms", "Backlash ceiling", $"must be positive, at most {BacklashPulseMaxMs} and at least the pulse width",
                a => a.BacklashCeilingMs is int ceiling && (ceiling <= 0 || ceiling > BacklashPulseMaxMs || (a.BacklashPulseMs is int pulse && ceiling < pulse)), NoBacklash),
            new("backlash_floor_ms", "Backlash floor", "must not exceed the ceiling",
                a => a.BacklashFloorMs is int floor && a.BacklashCeilingMs is int ceiling && floor > ceiling, NoBacklash),
            new("variable_delay_short_s", "Short delay", "must be whole seconds from 0 to 10",
                a => BadDelay(a.VariableDelayShortS, 10), NoVariableDelay),
            new("variable_delay_long_s", "Long delay", "must be whole seconds from 0 to 120",
                a => BadDelay(a.VariableDelayLongS, 120), NoVariableDelay),
            new("variable_delay_short_s", "Short delay", "must not exceed the long delay",
                a => a.VariableDelayShortS is double s && a.VariableDelayLongS is double l && s > l, NoVariableDelay),
            new("ra_algorithm", "RA algorithm", $"must be one of: {string.Join(", ", RaAlgorithms)}",
                a => Unknown(a.RaAlgorithm, RaAlgorithms), a => a with { RaAlgorithm = null }),
            new("dec_algorithm", "Dec algorithm", $"must be one of: {string.Join(", ", DecAlgorithms)}",
                a => Unknown(a.DecAlgorithm, DecAlgorithms), a => a with { DecAlgorithm = null }),
        ];

        /// <summary><paramref name="a"/> with every value outside the limits cleared, and what was wrong.</summary>
        public static (GuiderAdvancedSettings? Clean, IReadOnlyList<GuiderLimitViolation> Violations) Apply(GuiderAdvancedSettings? a) {
            var violations = new List<GuiderLimitViolation>();
            if (a is null) {
                return (null, violations);
            }
            var clean = a;
            foreach (var rule in Rules) {
                if (rule.Bad(clean)) {
                    violations.Add(new(rule.Field, rule.Setting, rule.Limit));
                    clean = rule.Clear(clean);
                }
            }
            return (clean, violations);
        }
    }
}
