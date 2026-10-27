#region "copyright"
/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/
#endregion "copyright"

using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Equipment.Equipment.MyGuider.PHD2 {

    /// <summary>
    /// #1364 item 4 — the guider's headless Guiding Assistant (openastro-guider #83) over the daemon's
    /// connection. The status comes back as the guider's raw JSON result: the daemon's service maps it,
    /// so a field the guider adds later costs nothing here. Refusals (no star, not calibrated, already
    /// running, a polar-alignment tool holding the star) surface as <see cref="GuiderRpcException"/>, which
    /// the endpoint layer turns into 422 with the guider's own words.
    /// </summary>
    public partial class PHD2Guider {

        /// <summary>The guider's bound on a start: the dialog samples for at least ten seconds and at
        /// most an hour (<c>duration_s</c> 10..3600).</summary>
        public const double GuidingAssistantMinDurationSec = 10;
        public const double GuidingAssistantMaxDurationSec = 3600;

        /// <summary>The start RPC's bound. Unlike a status poll, a start makes the guider check the star
        /// and the calibration and park its guide output before it answers, which a slow build or a
        /// busy SBC can stretch past the live view's 5 s; with that bound the daemon reported a run that
        /// had in fact begun as failed (review of #1389). Under the client's 30 s receive timeout for
        /// the start, so the daemon's own refusal, not the client's, is what the user reads.</summary>
        public const int GuidingAssistantStartTimeoutMs = 25000;

        /// <summary>True while the Guiding Assistant is measuring (drift phase or backlash test). PHD2
        /// reports "Guiding" for the whole run although nothing is being corrected, so the services that
        /// act on guiding (the PA residual, the star-lost watch, the state on the wire) read this first.
        /// Set from each assistant RPC's returned state; cleared on Disconnect.</summary>
        public bool GuidingAssistantActive { get; private set; }

        /// <summary>The guider's assistant states that mean a run is in progress (the contract's
        /// <c>measuring</c> and <c>measuring_backlash</c>); anything else (idle, done, failed, or no state)
        /// means it is not. Pure — unit-tested.</summary>
        public static bool IsGuidingAssistantRunning(string? state) =>
            state is "measuring" or "measuring_backlash";

        private void NoteGuidingAssistantState(JObject? result) =>
            GuidingAssistantActive = IsGuidingAssistantRunning(result?["state"]?.Type == JTokenType.String ? result["state"]!.Value<string>() : null);

        /// <summary>Start the measurement. <paramref name="durationSec"/> null = the guider's default
        /// (two minutes, what the dialog asks for).</summary>
        public async Task<JObject?> StartGuidingAssistantAsync(double? durationSec, bool measureBacklash, CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            RequireConnectedForLive();
            if (durationSec is double d && (!double.IsFinite(d) || d < GuidingAssistantMinDurationSec || d > GuidingAssistantMaxDurationSec)) {
                throw new ArgumentOutOfRangeException(nameof(durationSec), $"duration must be {GuidingAssistantMinDurationSec:F0}–{GuidingAssistantMaxDurationSec:F0} s");
            }
            var request = new Phd2GuidingAssistantStart {
                Parameters = new Phd2GuidingAssistantStartParameters { DurationSec = durationSec, MeasureBacklash = measureBacklash }
            };
            var response = await SendMessage<GenericPhdMethodResponse>(request, GuidingAssistantStartTimeoutMs).ConfigureAwait(false);
            ThrowOnError("guiding_assistant_start", response);
            var result = response.result as JObject;
            // Asymmetry on purpose: a start that answered without a state has still started a run (the
            // guider said yes), whereas a get_status without a state is an idle guider (nothing running
            // to report). The Progress/Done events correct either way within a sample.
            GuidingAssistantActive = result?["state"] is null || IsGuidingAssistantRunning(result["state"]?.Value<string>());
            return result;
        }

        public async Task<JObject?> StopGuidingAssistantAsync(CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            RequireConnectedForLive();
            var response = await SendMessage<GenericPhdMethodResponse>(new Phd2GuidingAssistantStop(), LiveRpcTimeoutMs).ConfigureAwait(false);
            ThrowOnError("guiding_assistant_stop", response);
            GuidingAssistantActive = false;
            return response.result as JObject;
        }

        /// <summary>The guider's status object, or null when it does not answer (not connected, or the
        /// guider predates the method). Quiet: the dialog polls it.</summary>
        public async Task<JObject?> GetGuidingAssistantStatusAsync(CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            if (!Connected) {
                return null;
            }
            var response = await SendMessage<GenericPhdMethodResponse>(new Phd2GuidingAssistantGetStatus(), LiveRpcTimeoutMs, notifyOnError: false).ConfigureAwait(false);
            if (response?.error != null) {
                return null;
            }
            var result = response?.result as JObject;
            NoteGuidingAssistantState(result);
            return result;
        }

        /// <summary>Write the named recommendations (<c>exposure</c>, <c>min_move_ra</c>, <c>min_move_dec</c>,
        /// <c>dec_backlash</c>, <c>dec_guide_mode</c>, <c>dec_algorithm</c>) to the guider's profile; returns
        /// the names it applied.</summary>
        public async Task<IReadOnlyList<string>> ApplyGuidingAssistantAsync(IReadOnlyList<string> which, CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(which);
            RequireConnectedForLive();
            if (which.Count == 0) {
                throw new ArgumentOutOfRangeException(nameof(which), "choose at least one recommendation to apply");
            }
            var request = new Phd2GuidingAssistantApply { Parameters = new Phd2GuidingAssistantApplyParameters { Which = which } };
            var response = await SendMessage<Phd2GuidingAssistantApplyResponse>(request, LiveRpcTimeoutMs).ConfigureAwait(false);
            ThrowOnError("guiding_assistant_apply", response);
            return response.result?.Applied ?? Array.Empty<string>();
        }

        /// <summary>
        /// #1448 — what the guider now holds for each applied recommendation, read back from the guider
        /// rather than taken from the recommendation (the exposure is a range the guider rounds to one of its
        /// durations; the backlash pulse is capped). Ara writes these into its profile so its next push sends
        /// them, not the values Apply just replaced. Best-effort: a getter the guider refuses leaves its
        /// field null.
        /// </summary>
        public async Task<GuidingAssistantAppliedValues> ReadGuidingAssistantAppliedAsync(IReadOnlyList<string> applied, CancellationToken ct) {
            ArgumentNullException.ThrowIfNull(applied);
            async Task<JToken?> Get(string method, Dictionary<string, object>? p = null) {
                ct.ThrowIfCancellationRequested();
                var resp = await SendMessage<GenericPhdMethodResponse>(new Phd2NamedGetter(method, p), LiveRpcTimeoutMs, notifyOnError: false).ConfigureAwait(false);
                return resp.error != null || resp.result is null ? null : JToken.FromObject(resp.result);
            }
            static double? D(JToken? t) => t?.Type is JTokenType.Float or JTokenType.Integer && double.IsFinite(t.Value<double>()) ? t.Value<double>() : null;
            static int? I(JToken? t) => D(t) is double d ? (int)Math.Round(d) : null;
            static string? S(JToken? t) => t?.Type == JTokenType.String && !string.IsNullOrWhiteSpace(t.Value<string>()) ? t.Value<string>() : null;
            bool Has(string name) => applied.Contains(name, StringComparer.Ordinal);

            var values = new GuidingAssistantAppliedValues();
            if (Has("exposure")) {
                values = values with { ExposureMs = I(await Get("get_exposure").ConfigureAwait(false)) };
            }
            // The algorithm first: the Dec minimum move below belongs to whichever algorithm Apply left.
            if (Has("dec_algorithm")) {
                values = values with { DecAlgorithm = S(await Get("get_algo", new() { ["axis"] = "dec" }).ConfigureAwait(false)) };
            }
            if (Has("min_move_ra")) {
                values = values with { MinMoveRaPx = D(await Get("get_algo_param", new() { ["axis"] = "ra", ["name"] = "minMove" }).ConfigureAwait(false)) };
            }
            if (Has("min_move_dec")) {
                values = values with { MinMoveDecPx = D(await Get("get_algo_param", new() { ["axis"] = "dec", ["name"] = "minMove" }).ConfigureAwait(false)) };
            }
            if (Has("dec_guide_mode")) {
                values = values with { DecGuideMode = S(await Get("get_dec_guide_mode").ConfigureAwait(false)) };
            }
            if (Has("dec_backlash")) {
                var b = await Get("get_backlash_comp").ConfigureAwait(false);
                values = values with {
                    BacklashEnabled = b?["Enabled"]?.Type == JTokenType.Boolean ? b["Enabled"]!.Value<bool>() : null,
                    BacklashPulseMs = I(b?["PulseWidth"]),
                    BacklashFloorMs = I(b?["Floor"]),
                    BacklashCeilingMs = I(b?["Ceiling"]),
                };
            }
            return values;
        }
    }

    /// <summary>#1448 — the guider's values after a Guiding Assistant Apply, in the guider's units (ms, px)
    /// and names (Dec mode "North", algorithm "Hysteresis"). Null = not applied, or the guider did not say.</summary>
    public sealed record GuidingAssistantAppliedValues {
        public int? ExposureMs { get; init; }
        public double? MinMoveRaPx { get; init; }
        public double? MinMoveDecPx { get; init; }
        public string? DecGuideMode { get; init; }
        public string? DecAlgorithm { get; init; }
        public bool? BacklashEnabled { get; init; }
        public int? BacklashPulseMs { get; init; }
        public int? BacklashFloorMs { get; init; }
        public int? BacklashCeilingMs { get; init; }
    }
}
