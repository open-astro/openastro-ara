#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Newtonsoft.Json;
using System.Collections.Generic;

namespace OpenAstroAra.Equipment.Equipment.MyGuider.PHD2 {

    // §45 polar alignment — named-object RPC requests for the guider surface ARA's PA routine
    // drives (openastro-guider design/POLAR_ALIGNMENT_DESIGN.md §8 + API_REFERENCE.md): solver
    // frames via capture_single_frame (fire-and-forget; completion + saved path ride the
    // SingleFrameComplete event), multi-star centroids for the live track loop, and the PA
    // session LEASE that stops anything else fighting ARA for the single-client guide camera
    // mid-routine. Same serialization-locked pattern as PHD2Methods.DarkLibrary (guider-e-4a);
    // ARA-side orchestration (state machine + ASTAP solve + mount slews) is the follow-up phase.

    /// <summary>
    /// <c>capture_single_frame {exposure?, binning?, gain?, subframe?, path?, save?}</c> — take one
    /// frame outside the guiding loop (camera connected, no active capture). Exposure is
    /// MILLISECONDS; omitted fields inherit the daemon's current camera settings. A non-empty
    /// absolute <c>path</c> implies <c>save</c>; the RPC result is an immediate <c>0</c> and the
    /// captured frame (with its saved-FITS path) is announced by the <c>SingleFrameComplete</c>
    /// event.
    /// <para>Same wire method as the NINA-era <c>Phd2CaptureSingleFrame</c> (PHD2Methods.cs —
    /// exposure + subframe only, and that file is ISO-8859-1 so it is not extended in place);
    /// this is the fork's FULL parameter surface, which the §45 solver-frame path needs
    /// (binning for transfer size, an absolute save path for ASTAP).</para>
    /// </summary>
    public class Phd2CaptureSolverFrame : Phd2Method<Phd2CaptureSolverFrameParameter> {
        public override string Method => "capture_single_frame";
    }

    public class Phd2CaptureSolverFrameParameter {

        [JsonProperty(PropertyName = "exposure", NullValueHandling = NullValueHandling.Ignore)]
        public int? ExposureMs { get; set; }

        [JsonProperty(PropertyName = "binning", NullValueHandling = NullValueHandling.Ignore)]
        public int? Binning { get; set; }

        // Daemon range 0..100 (its normalized gain scale, not e-/ADU).
        [JsonProperty(PropertyName = "gain", NullValueHandling = NullValueHandling.Ignore)]
        public int? Gain { get; set; }

        // [x, y, width, height]; omitted = full frame (what the solver wants).
        [JsonProperty(PropertyName = "subframe", NullValueHandling = NullValueHandling.Ignore)]
        public IReadOnlyList<int>? Subframe { get; set; }

        // Absolute path for the saved FITS; the daemon rejects a relative path or an existing file.
        [JsonProperty(PropertyName = "path", NullValueHandling = NullValueHandling.Ignore)]
        public string? Path { get; set; }

        [JsonProperty(PropertyName = "save", NullValueHandling = NullValueHandling.Ignore)]
        public bool? Save { get; set; }
    }

    /// <summary>
    /// <c>get_star_centroids {roi?, max_stars?}</c> — detect stars on the CURRENT frame and return
    /// their sub-pixel centroids without selecting a star or touching guider state (unlike
    /// <c>find_star</c>). Result: an array of <c>{x, y, snr, mass, hfd}</c>. Rejected on a
    /// subframe image; <c>max_stars</c> is daemon-clamped to 1..50 (default 12).
    /// </summary>
    public class Phd2GetStarCentroids : Phd2Method<Phd2GetStarCentroidsParameter> {
        public override string Method => "get_star_centroids";
    }

    public class Phd2GetStarCentroidsParameter {

        // [x, y, width, height]; omitted = whole frame.
        [JsonProperty(PropertyName = "roi", NullValueHandling = NullValueHandling.Ignore)]
        public IReadOnlyList<int>? Roi { get; set; }

        [JsonProperty(PropertyName = "max_stars", NullValueHandling = NullValueHandling.Ignore)]
        public int? MaxStars { get; set; }
    }

    /// <summary>
    /// <c>set_pa_session {active, timeout_s?}</c> — start/renew (active=true) or end (false) the
    /// polar-alignment session lease. While active the daemon rejects <c>guide</c>/<c>loop</c>/
    /// <c>dither</c>/<c>guide_pulse</c>/calibration builds/<c>set_connected</c> with
    /// "polar-alignment session in progress", keeping the single-client guide camera ARA's for
    /// the routine; the lease AUTO-EXPIRES (10–3600 s, daemon default 600) so a crashed
    /// orchestrator can't wedge the daemon — renew by calling again. Starting is rejected while
    /// calibrating or guiding.
    /// </summary>
    public class Phd2SetPaSession : Phd2Method<Phd2SetPaSessionParameter> {
        public override string Method => "set_pa_session";
    }

    public class Phd2SetPaSessionParameter {

        [JsonProperty(PropertyName = "active")]
        public bool Active { get; set; }

        [JsonProperty(PropertyName = "timeout_s", NullValueHandling = NullValueHandling.Ignore)]
        public int? TimeoutS { get; set; }
    }

    /// <summary><c>get_pa_session</c> (no params) — <c>{active, expires_in_s?}</c>.</summary>
    public class Phd2GetPaSession : Phd2Method {
        public override string Method => "get_pa_session";
    }
}
