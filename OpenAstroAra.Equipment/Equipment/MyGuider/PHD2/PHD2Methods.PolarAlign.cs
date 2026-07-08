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

    // §45 Static PA — the near-pole centre-of-rotation "dot-to-bullseye" tool (openastro-guider
    // design/POLAR_ALIGNMENT_DESIGN.md; API_REFERENCE.md "Static PA"). ARA drives it headlessly and
    // renders the reticle from the status object below; the daemon owns the geometry, ARA owns the solver
    // + slews. All five methods (start/measure/get_status/stop) return the SAME status object; close
    // returns 0 (reuse GenericPhdMethodResponse). Every status field is nullable: the daemon emits them
    // conditionally (e.g. rotation only while aligning+auto; current_star/live_adjustment only when the
    // live star is valid; the object short-circuits when active=false or calced=false). This is the DTO
    // half of the §45 client — the PolarAlignService wiring is a later slice, DTO-first per the precedent.

    /// <summary><c>staticpa_start {auto?, hemisphere?, ref_star?, hour_angle?, flip_camera?}</c> — begin
    /// the Static PA routine (auto mode needs a slewable mount). Returns the status object.</summary>
    public class Phd2StaticPaStart : Phd2Method<Phd2StaticPaStartParameter> {
        public override string Method => "staticpa_start";
    }

    public class Phd2StaticPaStartParameter {

        [JsonProperty(PropertyName = "auto", NullValueHandling = NullValueHandling.Ignore)]
        public bool? Auto { get; set; }

        // "north" / "south".
        [JsonProperty(PropertyName = "hemisphere", NullValueHandling = NullValueHandling.Ignore)]
        public string? Hemisphere { get; set; }

        // Index into the status object's ref_stars (0..size-1).
        [JsonProperty(PropertyName = "ref_star", NullValueHandling = NullValueHandling.Ignore)]
        public int? RefStar { get; set; }

        // Hours, 0..24.
        [JsonProperty(PropertyName = "hour_angle", NullValueHandling = NullValueHandling.Ignore)]
        public double? HourAngle { get; set; }

        [JsonProperty(PropertyName = "flip_camera", NullValueHandling = NullValueHandling.Ignore)]
        public bool? FlipCamera { get; set; }
    }

    /// <summary><c>staticpa_measure {position}</c> — record a measurement point (manual mode only;
    /// <c>position</c> is 2 or 3, rotating RA ≥ 0h20m between points). Returns the status object.</summary>
    public class Phd2StaticPaMeasure : Phd2Method<Phd2StaticPaMeasureParameter> {
        public override string Method => "staticpa_measure";
    }

    public class Phd2StaticPaMeasureParameter {

        // 2 or 3 — required, so it's sent unconditionally.
        [JsonProperty(PropertyName = "position")]
        public int Position { get; set; }
    }

    /// <summary><c>staticpa_get_status</c> (no params) — the full Static PA status object.</summary>
    public class Phd2StaticPaGetStatus : Phd2Method {
        public override string Method => "staticpa_get_status";
    }

    /// <summary><c>staticpa_stop</c> (no params) — stop aligning; returns the status object.</summary>
    public class Phd2StaticPaStop : Phd2Method {
        public override string Method => "staticpa_stop";
    }

    /// <summary><c>staticpa_close</c> (no params) — tear the tool down; returns <c>0</c>
    /// (deserialize with <see cref="GenericPhdMethodResponse"/>).</summary>
    public class Phd2StaticPaClose : Phd2Method {
        public override string Method => "staticpa_close";
    }

    /// <summary>Response wrapper for <c>staticpa_start</c>/<c>_measure</c>/<c>_get_status</c>/<c>_stop</c>
    /// — all four return the same <see cref="Phd2StaticPaStatus"/>.</summary>
    public class Phd2StaticPaStatusResponse : PhdMethodResponse {
        public Phd2StaticPaStatus? result { get; set; }
    }

    /// <summary>The Static PA status object. Fields are nullable to mirror the daemon's conditional
    /// emission — <c>Active=false</c> or <c>Calced=false</c> short-circuits the object, and the
    /// centre/adjustment/reticle group only appears once <c>Calced</c>.</summary>
    public class Phd2StaticPaStatus {

        [JsonProperty(PropertyName = "active")]
        public bool? Active { get; set; }

        [JsonProperty(PropertyName = "aligning")]
        public bool? Aligning { get; set; }

        [JsonProperty(PropertyName = "auto")]
        public bool? Auto { get; set; }

        [JsonProperty(PropertyName = "can_slew")]
        public bool? CanSlew { get; set; }

        [JsonProperty(PropertyName = "hemisphere")]
        public string? Hemisphere { get; set; }

        [JsonProperty(PropertyName = "hour_angle")]
        public double? HourAngle { get; set; }

        [JsonProperty(PropertyName = "flip_camera")]
        public bool? FlipCamera { get; set; }

        [JsonProperty(PropertyName = "pixel_scale")]
        public double? PixelScale { get; set; }

        [JsonProperty(PropertyName = "camera_angle")]
        public double? CameraAngle { get; set; }

        [JsonProperty(PropertyName = "ref_star")]
        public int? RefStar { get; set; }

        // The 8 near-pole catalog stars ARA offers as the alignment reference.
        [JsonProperty(PropertyName = "ref_stars")]
        public IReadOnlyList<Phd2StaticPaRefStar>? RefStars { get; set; }

        // 0–3 recorded points (manual mode).
        [JsonProperty(PropertyName = "measured_points")]
        public IReadOnlyList<Phd2StaticPaMeasuredPoint>? MeasuredPoints { get; set; }

        // Present only while aligning in auto mode.
        [JsonProperty(PropertyName = "rotation")]
        public Phd2StaticPaRotation? Rotation { get; set; }

        [JsonProperty(PropertyName = "calced")]
        public bool? Calced { get; set; }

        // The following appear only once Calced is true.
        [JsonProperty(PropertyName = "centre")]
        public Phd2StaticPaCentre? Centre { get; set; }

        [JsonProperty(PropertyName = "adjustment")]
        public Phd2StaticPaAdjustment? Adjustment { get; set; }

        [JsonProperty(PropertyName = "ref_star_target")]
        public Phd2Point? RefStarTarget { get; set; }

        // current_star + live_adjustment are emitted together, only when the live star position is valid.
        [JsonProperty(PropertyName = "current_star")]
        public Phd2Point? CurrentStar { get; set; }

        [JsonProperty(PropertyName = "live_adjustment")]
        public Phd2StaticPaAdjustment? LiveAdjustment { get; set; }
    }

    /// <summary>An <c>{x, y}</c> point in image pixels — shared across the polar-align result shapes.</summary>
    public class Phd2Point {

        [JsonProperty(PropertyName = "x")]
        public double? X { get; set; }

        [JsonProperty(PropertyName = "y")]
        public double? Y { get; set; }
    }

    public class Phd2StaticPaRefStar {

        [JsonProperty(PropertyName = "index")]
        public int? Index { get; set; }

        [JsonProperty(PropertyName = "name")]
        public string? Name { get; set; }

        [JsonProperty(PropertyName = "ra")]
        public double? Ra { get; set; }

        [JsonProperty(PropertyName = "dec")]
        public double? Dec { get; set; }

        [JsonProperty(PropertyName = "mag")]
        public double? Mag { get; set; }
    }

    public class Phd2StaticPaMeasuredPoint {

        [JsonProperty(PropertyName = "position")]
        public int? Position { get; set; }

        [JsonProperty(PropertyName = "x")]
        public double? X { get; set; }

        [JsonProperty(PropertyName = "y")]
        public double? Y { get; set; }
    }

    public class Phd2StaticPaRotation {

        [JsonProperty(PropertyName = "required_deg")]
        public double? RequiredDeg { get; set; }

        [JsonProperty(PropertyName = "rotated_deg")]
        public double? RotatedDeg { get; set; }

        [JsonProperty(PropertyName = "step")]
        public int? Step { get; set; }

        [JsonProperty(PropertyName = "required_steps")]
        public int? RequiredSteps { get; set; }

        [JsonProperty(PropertyName = "slewing")]
        public bool? Slewing { get; set; }
    }

    public class Phd2StaticPaCentre {

        [JsonProperty(PropertyName = "x")]
        public double? X { get; set; }

        [JsonProperty(PropertyName = "y")]
        public double? Y { get; set; }

        [JsonProperty(PropertyName = "radius_px")]
        public double? RadiusPx { get; set; }
    }

    /// <summary>An alt/az mount-error decomposition at a point — pixels + arcminutes + the on-sensor push
    /// vectors. Reused for both the static <c>adjustment</c> and the live <c>live_adjustment</c>.</summary>
    public class Phd2StaticPaAdjustment {

        [JsonProperty(PropertyName = "alt_error_px")]
        public double? AltErrorPx { get; set; }

        [JsonProperty(PropertyName = "az_error_px")]
        public double? AzErrorPx { get; set; }

        [JsonProperty(PropertyName = "total_error_px")]
        public double? TotalErrorPx { get; set; }

        [JsonProperty(PropertyName = "alt_error_arcmin")]
        public double? AltErrorArcmin { get; set; }

        [JsonProperty(PropertyName = "az_error_arcmin")]
        public double? AzErrorArcmin { get; set; }

        [JsonProperty(PropertyName = "total_error_arcmin")]
        public double? TotalErrorArcmin { get; set; }

        [JsonProperty(PropertyName = "az_vector")]
        public Phd2Point? AzVector { get; set; }

        [JsonProperty(PropertyName = "alt_vector")]
        public Phd2Point? AltVector { get; set; }
    }

    // §45 Polar Drift — the drift-based polar-align tool: pick a star near the meridian/equator, let the
    // mount track, and watch it drift; the daemon fits the drift into an az-error vector pointing at the
    // pole (openastro-guider src/polardrift_toolwin.cpp; event_server.cpp polardrift_* handlers). Simpler
    // than Static PA — no measure step and no reference-star catalog: start/get_status/stop share one flat
    // status object, close returns 0. Only start takes params. Every status field is nullable to mirror the
    // daemon's conditional emission: !active short-circuits the object, the drift results (offset/error/
    // direction/star/target) appear only once num_samples > 1, and elapsed_s only while drifting.

    /// <summary><c>polardrift_start {hemisphere?, mirrored?}</c> — begin the drift routine (both params
    /// optional; absent keeps the tool's current/profile value). Returns the status object.</summary>
    public class Phd2PolarDriftStart : Phd2Method<Phd2PolarDriftStartParameter> {
        public override string Method => "polardrift_start";
    }

    public class Phd2PolarDriftStartParameter {

        // "north" / "south" — the daemon rejects any other string.
        [JsonProperty(PropertyName = "hemisphere", NullValueHandling = NullValueHandling.Ignore)]
        public string? Hemisphere { get; set; }

        // Set when the optical train mirrors the image (e.g. a star diagonal).
        [JsonProperty(PropertyName = "mirrored", NullValueHandling = NullValueHandling.Ignore)]
        public bool? Mirrored { get; set; }
    }

    /// <summary><c>polardrift_get_status</c> (no params) — the full Polar Drift status object.</summary>
    public class Phd2PolarDriftGetStatus : Phd2Method {
        public override string Method => "polardrift_get_status";
    }

    /// <summary><c>polardrift_stop</c> (no params) — stop drifting; returns the status object.</summary>
    public class Phd2PolarDriftStop : Phd2Method {
        public override string Method => "polardrift_stop";
    }

    /// <summary><c>polardrift_close</c> (no params) — tear the tool down; returns <c>0</c>
    /// (deserialize with <see cref="GenericPhdMethodResponse"/>).</summary>
    public class Phd2PolarDriftClose : Phd2Method {
        public override string Method => "polardrift_close";
    }

    /// <summary>Response wrapper for <c>polardrift_start</c>/<c>_get_status</c>/<c>_stop</c> — all three
    /// return the same <see cref="Phd2PolarDriftStatus"/>.</summary>
    public class Phd2PolarDriftStatusResponse : PhdMethodResponse {
        public Phd2PolarDriftStatus? result { get; set; }
    }

    /// <summary>The Polar Drift status object. Fields are nullable to mirror the daemon's conditional
    /// emission — <c>Active=false</c> short-circuits the object, the drift solution
    /// (<c>OffsetPx</c>/<c>ErrorArcmin</c>/<c>PoleDirectionDeg</c>/<c>CurrentStar</c>/<c>Target</c>) only
    /// appears once at least two samples exist, and <c>ElapsedS</c> only while drifting.</summary>
    public class Phd2PolarDriftStatus {

        [JsonProperty(PropertyName = "active")]
        public bool? Active { get; set; }

        [JsonProperty(PropertyName = "drifting")]
        public bool? Drifting { get; set; }

        [JsonProperty(PropertyName = "hemisphere")]
        public string? Hemisphere { get; set; }

        [JsonProperty(PropertyName = "mirrored")]
        public bool? Mirrored { get; set; }

        [JsonProperty(PropertyName = "pixel_scale")]
        public double? PixelScale { get; set; }

        [JsonProperty(PropertyName = "num_samples")]
        public int? NumSamples { get; set; }

        // Present only while drifting with at least one sample.
        [JsonProperty(PropertyName = "elapsed_s")]
        public double? ElapsedS { get; set; }

        // The following make up the drift solution — present only once num_samples > 1.
        [JsonProperty(PropertyName = "offset_px")]
        public double? OffsetPx { get; set; }

        [JsonProperty(PropertyName = "error_arcmin")]
        public double? ErrorArcmin { get; set; }

        [JsonProperty(PropertyName = "pole_direction_deg")]
        public double? PoleDirectionDeg { get; set; }

        // current_star + target are each {x, y} — additionally gated on the respective position being valid.
        [JsonProperty(PropertyName = "current_star")]
        public Phd2Point? CurrentStar { get; set; }

        [JsonProperty(PropertyName = "target")]
        public Phd2Point? Target { get; set; }
    }

    // §45 Drift Align — the classic two-axis drift method (azimuth then altitude): watch the Dec drift of a
    // guided star near the meridian/horizon, adjust the mount, repeat (openastro-guider src/drift_tool.cpp;
    // event_server.cpp driftalign_* handlers). The richest of the three families — start/set_phase/drift/
    // adjust/get_status share one status object, close returns 0. Only set_phase takes a param (which axis);
    // drift/adjust are mode switches (looping-to-measure vs looping-to-adjust). Every status field bar
    // `active` is nullable to mirror the daemon's conditional emission: !active short-circuits the object,
    // and status_message / polar_alignment_error / current_star / lock_position / scope are each further
    // gated (non-empty message, a computed alignment error, a valid star/lock, a connected pointing source).

    /// <summary><c>driftalign_start</c> (no params) — open the Drift Align tool. Returns the status object.</summary>
    public class Phd2DriftAlignStart : Phd2Method {
        public override string Method => "driftalign_start";
    }

    /// <summary><c>driftalign_set_phase {phase}</c> — switch the adjustment axis (<c>"azimuth"</c> or
    /// <c>"altitude"</c>; the daemon rejects anything else). Returns the status object.</summary>
    public class Phd2DriftAlignSetPhase : Phd2Method<Phd2DriftAlignSetPhaseParameter> {
        public override string Method => "driftalign_set_phase";
    }

    public class Phd2DriftAlignSetPhaseParameter {

        // "azimuth" / "altitude" — required, so it's sent unconditionally.
        [JsonProperty(PropertyName = "phase")]
        public string? Phase { get; set; }
    }

    /// <summary><c>driftalign_drift</c> (no params) — enter drift-measure mode. Returns the status object.</summary>
    public class Phd2DriftAlignDrift : Phd2Method {
        public override string Method => "driftalign_drift";
    }

    /// <summary><c>driftalign_adjust</c> (no params) — enter adjust mode. Returns the status object.</summary>
    public class Phd2DriftAlignAdjust : Phd2Method {
        public override string Method => "driftalign_adjust";
    }

    /// <summary><c>driftalign_get_status</c> (no params) — the full Drift Align status object.</summary>
    public class Phd2DriftAlignGetStatus : Phd2Method {
        public override string Method => "driftalign_get_status";
    }

    /// <summary><c>driftalign_close</c> (no params) — tear the tool down; returns <c>0</c>
    /// (deserialize with <see cref="GenericPhdMethodResponse"/>).</summary>
    public class Phd2DriftAlignClose : Phd2Method {
        public override string Method => "driftalign_close";
    }

    /// <summary>Response wrapper for <c>driftalign_start</c>/<c>_set_phase</c>/<c>_drift</c>/<c>_adjust</c>/
    /// <c>_get_status</c> — all five return the same <see cref="Phd2DriftAlignStatus"/>.</summary>
    public class Phd2DriftAlignStatusResponse : PhdMethodResponse {
        public Phd2DriftAlignStatus? result { get; set; }
    }

    /// <summary>The Drift Align status object. Fields are nullable to mirror the daemon's conditional
    /// emission — <c>Active=false</c> short-circuits the object, and <c>StatusMessage</c> /
    /// <c>PolarAlignmentError</c> / <c>CurrentStar</c> / <c>LockPosition</c> / <c>Scope</c> are each
    /// further gated on their underlying state being present.</summary>
    public class Phd2DriftAlignStatus {

        [JsonProperty(PropertyName = "active")]
        public bool? Active { get; set; }

        // "azimuth" / "altitude".
        [JsonProperty(PropertyName = "phase")]
        public string? Phase { get; set; }

        // "drift" / "adjust" / "idle".
        [JsonProperty(PropertyName = "mode")]
        public string? Mode { get; set; }

        [JsonProperty(PropertyName = "drifting")]
        public bool? Drifting { get; set; }

        [JsonProperty(PropertyName = "can_slew")]
        public bool? CanSlew { get; set; }

        [JsonProperty(PropertyName = "slewing")]
        public bool? Slewing { get; set; }

        [JsonProperty(PropertyName = "calibrated")]
        public bool? Calibrated { get; set; }

        [JsonProperty(PropertyName = "guiding")]
        public bool? Guiding { get; set; }

        // Present only when the tool has a non-empty status line.
        [JsonProperty(PropertyName = "status_message")]
        public string? StatusMessage { get; set; }

        // Present only once the daemon can compute an alignment error from the guide graph.
        [JsonProperty(PropertyName = "polar_alignment_error")]
        public Phd2DriftAlignError? PolarAlignmentError { get; set; }

        // {x, y} — gated on a valid current star / lock position.
        [JsonProperty(PropertyName = "current_star")]
        public Phd2Point? CurrentStar { get; set; }

        [JsonProperty(PropertyName = "lock_position")]
        public Phd2Point? LockPosition { get; set; }

        // Present only when the pointing source is connected and reports coordinates.
        [JsonProperty(PropertyName = "scope")]
        public Phd2DriftAlignScope? Scope { get; set; }
    }

    /// <summary>The Drift Align alignment-error read — the arcmin polar error plus the Dec drift rate it
    /// was fitted from.</summary>
    public class Phd2DriftAlignError {

        [JsonProperty(PropertyName = "error_arcmin")]
        public double? ErrorArcmin { get; set; }

        [JsonProperty(PropertyName = "dec_drift_arcsec_per_min")]
        public double? DecDriftArcsecPerMin { get; set; }

        [JsonProperty(PropertyName = "samples")]
        public int? Samples { get; set; }
    }

    /// <summary>The pointing source's current position at the time of the status read.</summary>
    public class Phd2DriftAlignScope {

        [JsonProperty(PropertyName = "ra_hours")]
        public double? RaHours { get; set; }

        [JsonProperty(PropertyName = "dec_degrees")]
        public double? DecDegrees { get; set; }

        [JsonProperty(PropertyName = "lst_hours")]
        public double? LstHours { get; set; }
    }
}
