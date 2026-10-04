#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace OpenAstroAra.Equipment.Equipment.MyGuider.PHD2.PhdEvents {

    /// <summary>
    /// The PHD2 guiding-session events that are NOT guide steps — the things PHD2's own graph
    /// annotates (dither, settling, settle done, star lost) plus the session transitions around
    /// them. <see cref="PHD2Guider.MarkerEvent"/> raises one per PHD2 event message so a
    /// client can draw PHD2's markers from the same source PHD2 draws them.
    /// </summary>
    public sealed class PhdGuiderMarkerEventArgs : System.EventArgs {

        /// <summary>
        /// <c>dithered</c> | <c>settling</c> | <c>settle_done</c> | <c>star_lost</c> |
        /// <c>calibration_started</c> | <c>calibration_complete</c> | <c>calibration_failed</c> |
        /// <c>guiding_started</c> | <c>guiding_stopped</c> | <c>paused</c> | <c>resumed</c> |
        /// <c>lock_position_lost</c>.
        /// </summary>
        public required string Kind { get; init; }

        /// <summary>Dither offset in guide-camera pixels (<c>dithered</c>).</summary>
        public double? Dx { get; init; }
        public double? Dy { get; init; }

        /// <summary>Current star distance from the lock position in px (<c>settling</c>, <c>star_lost</c>).</summary>
        public double? Distance { get; init; }

        /// <summary>Seconds settled so far / required settle time (<c>settling</c>).</summary>
        public double? TimeSec { get; init; }
        public double? SettleTimeSec { get; init; }

        /// <summary>PHD2's status code (<c>settle_done</c>: 0 = ok; <c>star_lost</c>: error code).</summary>
        public int? Status { get; init; }

        /// <summary>Error text when the settle failed or the star was lost (empty = none).</summary>
        public string? Error { get; init; }

        public double? StarMass { get; init; }
        public double? Snr { get; init; }
        public int? Frame { get; init; }
    }
}
