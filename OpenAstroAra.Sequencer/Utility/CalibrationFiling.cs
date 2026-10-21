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
using System.Globalization;

namespace OpenAstroAra.Sequencer.Utility {

    /// <summary>
    /// Where a generated calibration run files its frames: under the lights' target and night, so
    /// the flats, darks and bias sit next to the lights they calibrate
    /// (<c>{target}/{night}/Flat</c>, <c>…/Dark</c>, <c>…/Bias</c>) instead of under the step's name
    /// and the night the calibration happened to be shot. The capture steps carry it as two
    /// optional JSON fields, <c>CalibrationTarget</c> and <c>CalibrationNight</c> (yyyy-MM-dd).
    /// </summary>
    public static class CalibrationFiling {

        public const string NightFormat = "yyyy-MM-dd";

        /// <summary>The night a step files under, or null for a missing or malformed value (the
        /// frame then files under the night it was taken).</summary>
        public static DateOnly? ParseNight(string? night) =>
            DateOnly.TryParseExact(night, NightFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

        /// <summary>The target a step files under, or null when it doesn't name one.</summary>
        public static string? Target(string? target) => string.IsNullOrWhiteSpace(target) ? null : target;
    }
}
