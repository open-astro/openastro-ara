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
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Equipment.Equipment.MyGuider.PHD2 {

    /// <summary>
    /// #1364 (epic item 3) — the guider's settings beyond Tune Guiding, as Ara keeps them in its profile.
    /// Every field is optional: null means "leave the guider's own value", so a profile that never touched
    /// a setting never overwrites it. Units are the guider's: durations in ms, detection in px, SNR plain.
    /// </summary>
    public sealed record GuiderAdvancedSettings {
        public bool? MultiStarEnabled { get; init; }
        public bool? FastRecenter { get; init; }
        public bool? TolerateJumps { get; init; }
        public double? TolerateJumpsThresholdPx { get; init; }
        public double? MinStarHfd { get; init; }
        public double? MaxStarHfd { get; init; }
        public double? MinStarSnr { get; init; }
        public bool? MassChangeThresholdEnabled { get; init; }
        public double? MassChangeThreshold { get; init; }
        public int? SearchRegionPx { get; init; }
        public int? AutoExposureMinMs { get; init; }
        public int? AutoExposureMaxMs { get; init; }
        public double? AutoExposureTargetSnr { get; init; }
        /// <summary>The guider's names: "None", "2x2 Mean", "3x3 Median".</summary>
        public string? NoiseReduction { get; init; }
        public bool? UseSubframes { get; init; }
        public int? MaxRaDurationMs { get; init; }
        public int? MaxDecDurationMs { get; init; }
        public bool? BacklashEnabled { get; init; }
        public int? BacklashPulseMs { get; init; }
        public int? BacklashFloorMs { get; init; }
        public int? BacklashCeilingMs { get; init; }
        public bool? AssumeOrthogonal { get; init; }
        public bool? StopGuidingWhenSlewing { get; init; }
        public bool? CalFlipRequiresDecFlip { get; init; }
        public bool? VariableDelayEnabled { get; init; }
        public double? VariableDelayShortS { get; init; }
        public double? VariableDelayLongS { get; init; }
        public double? DitherScaleFactor { get; init; }
        public bool? DitherRaOnly { get; init; }
        /// <summary>The guider's algorithm names (get_algos), e.g. "Hysteresis", "Predictive PEC".</summary>
        public string? RaAlgorithm { get; init; }
        public string? DecAlgorithm { get; init; }
    }

    /// <summary>A setter whose parameters are a named object built at runtime — the settings groups each
    /// take a subset of keys, and only the ones Ara sets are sent.</summary>
    public sealed class Phd2NamedSetter : Phd2Method<Dictionary<string, object>> {
        private readonly string _method;

        public Phd2NamedSetter(string method, Dictionary<string, object> parameters) {
            _method = method;
            Parameters = parameters;
        }

        public override string Method => _method;
    }

    public class Phd2NamedGetter : Phd2Method<Dictionary<string, object>> {
        private readonly string _method;

        public Phd2NamedGetter(string method, Dictionary<string, object>? parameters = null) {
            _method = method;
            Parameters = parameters;
        }

        public override string Method => _method;
    }

    public partial class PHD2Guider {

        /// <summary>Where the guider reads Ara's advanced settings on every push; null = none kept.
        /// GuiderService wires it to the profile store.</summary>
        public Func<GuiderAdvancedSettings?>? AdvancedSettings { get; set; }

        private GuiderAdvancedSettings? AdvancedSettingsQuietly() {
            try {
                return AdvancedSettings?.Invoke();
            } catch (InvalidOperationException) {
                return null;
            }
        }

        /// <summary>
        /// The runtime setter messages for <paramref name="a"/>, in the guider's groups: only the keys Ara
        /// set are sent. Multi-star is not here — it rides <c>set_profile_setup</c>, which the guider only
        /// accepts with its equipment disconnected (see the engine-config builder). Variable delay is sent
        /// only with all three of its values (the guider requires them together). Pure — unit-tested.
        /// </summary>
        public static IReadOnlyList<Phd2Method> BuildAdvancedSettingsMessages(GuiderAdvancedSettings? a) {
            var messages = new List<Phd2Method>();
            if (a is null) {
                return messages;
            }
            void Group(string method, params (string Key, object? Value)[] values) {
                var p = new Dictionary<string, object>();
                foreach (var (key, value) in values) {
                    if (value is not null) {
                        p[key] = value;
                    }
                }
                if (p.Count > 0) {
                    messages.Add(new Phd2NamedSetter(method, p));
                }
            }
            Group("set_guider_options",
                ("FastRecenter", a.FastRecenter),
                ("TolerateJumps", a.TolerateJumps),
                ("TolerateJumpsThreshold", a.TolerateJumpsThresholdPx));
            Group("set_star_detection",
                ("MinStarHFD", a.MinStarHfd),
                ("MaxStarHFD", a.MaxStarHfd),
                ("MinStarSNR", a.MinStarSnr),
                ("MassChangeThresholdEnabled", a.MassChangeThresholdEnabled),
                ("MassChangeThreshold", a.MassChangeThreshold),
                ("SearchRegion", a.SearchRegionPx));
            Group("set_auto_exposure",
                ("MinExposure", a.AutoExposureMinMs),
                ("MaxExposure", a.AutoExposureMaxMs),
                ("TargetSNR", a.AutoExposureTargetSnr));
            if (!string.IsNullOrWhiteSpace(a.NoiseReduction)) {
                Group("set_noise_reduction", ("method", a.NoiseReduction.Trim()));
            }
            Group("set_use_subframes", ("enabled", a.UseSubframes));
            Group("set_guide_limits",
                ("MaxRaDuration", a.MaxRaDurationMs),
                ("MaxDecDuration", a.MaxDecDurationMs));
            Group("set_backlash_comp",
                ("Enabled", a.BacklashEnabled),
                ("PulseWidth", a.BacklashPulseMs),
                ("Floor", a.BacklashFloorMs),
                ("Ceiling", a.BacklashCeilingMs));
            Group("set_mount_options",
                ("AssumeOrthogonal", a.AssumeOrthogonal),
                ("StopGuidingWhenSlewing", a.StopGuidingWhenSlewing),
                ("CalFlipRequiresDecFlip", a.CalFlipRequiresDecFlip));
            if (a.VariableDelayEnabled is not null && a.VariableDelayShortS is not null && a.VariableDelayLongS is not null) {
                Group("set_variable_delay_settings",
                    ("Enabled", a.VariableDelayEnabled),
                    ("ShortDelaySeconds", a.VariableDelayShortS),
                    ("LongDelaySeconds", a.VariableDelayLongS));
            }
            Group("set_dither_settings",
                ("ScaleFactor", a.DitherScaleFactor),
                ("RaOnly", a.DitherRaOnly));
            if (!string.IsNullOrWhiteSpace(a.RaAlgorithm)) {
                Group("set_algo", ("axis", "ra"), ("name", a.RaAlgorithm.Trim()));
            }
            if (!string.IsNullOrWhiteSpace(a.DecAlgorithm)) {
                Group("set_algo", ("axis", "dec"), ("name", a.DecAlgorithm.Trim()));
            }
            return messages;
        }

        /// <summary>
        /// What the guider has now, for Ara's settings page to show beside the profile's values. Every
        /// group is read best-effort: a getter the guider refuses (no mount defined for the algorithms, an
        /// older build) leaves its fields null. Null when the guider is not connected.
        /// </summary>
        public async Task<GuiderAdvancedSettings?> ReadAdvancedSettingsAsync(CancellationToken ct) {
            if (!Connected) {
                return null;
            }
            async Task<JToken?> Get(string method, Dictionary<string, object>? p = null) {
                ct.ThrowIfCancellationRequested();
                var resp = await SendMessage<GenericPhdMethodResponse>(new Phd2NamedGetter(method, p), LiveRpcTimeoutMs).ConfigureAwait(false);
                return resp.error != null || resp.result is null ? null : JToken.FromObject(resp.result);
            }
            static bool? B(JToken? o, string k) => o?[k]?.Type == JTokenType.Boolean ? o[k]!.Value<bool>() : null;
            static double? D(JToken? o, string k) => o?[k]?.Type is JTokenType.Float or JTokenType.Integer ? o[k]!.Value<double>() : null;
            static int? I(JToken? o, string k) => D(o, k) is double d ? (int)Math.Round(d) : null;

            var options = await Get("get_guider_options").ConfigureAwait(false);
            var setup = await Get("get_profile_setup").ConfigureAwait(false);
            var detection = await Get("get_star_detection").ConfigureAwait(false);
            var autoExp = await Get("get_auto_exposure").ConfigureAwait(false);
            var noise = await Get("get_noise_reduction").ConfigureAwait(false);
            var subframes = await Get("get_use_subframes").ConfigureAwait(false);
            var limits = await Get("get_guide_limits").ConfigureAwait(false);
            var backlash = await Get("get_backlash_comp").ConfigureAwait(false);
            var mount = await Get("get_mount_options").ConfigureAwait(false);
            var delay = await Get("get_variable_delay_settings").ConfigureAwait(false);
            var dither = await Get("get_dither_settings").ConfigureAwait(false);
            var raAlgo = await Get("get_algo", new() { ["axis"] = "ra" }).ConfigureAwait(false);
            var decAlgo = await Get("get_algo", new() { ["axis"] = "dec" }).ConfigureAwait(false);

            return new GuiderAdvancedSettings {
                MultiStarEnabled = B(setup, "multistar_enabled"),
                FastRecenter = B(options, "FastRecenter"),
                TolerateJumps = B(options, "TolerateJumps"),
                TolerateJumpsThresholdPx = D(options, "TolerateJumpsThreshold"),
                MinStarHfd = D(detection, "MinStarHFD"),
                MaxStarHfd = D(detection, "MaxStarHFD"),
                MinStarSnr = D(detection, "MinStarSNR"),
                MassChangeThresholdEnabled = B(detection, "MassChangeThresholdEnabled"),
                MassChangeThreshold = D(detection, "MassChangeThreshold"),
                SearchRegionPx = I(detection, "SearchRegion"),
                AutoExposureMinMs = I(autoExp, "MinExposure"),
                AutoExposureMaxMs = I(autoExp, "MaxExposure"),
                AutoExposureTargetSnr = D(autoExp, "TargetSNR"),
                NoiseReduction = noise?.Type == JTokenType.String ? noise.Value<string>() : null,
                UseSubframes = subframes?.Type == JTokenType.Boolean ? subframes.Value<bool>() : null,
                MaxRaDurationMs = I(limits, "MaxRaDuration"),
                MaxDecDurationMs = I(limits, "MaxDecDuration"),
                BacklashEnabled = B(backlash, "Enabled"),
                BacklashPulseMs = I(backlash, "PulseWidth"),
                BacklashFloorMs = I(backlash, "Floor"),
                BacklashCeilingMs = I(backlash, "Ceiling"),
                AssumeOrthogonal = B(mount, "AssumeOrthogonal"),
                StopGuidingWhenSlewing = B(mount, "StopGuidingWhenSlewing"),
                CalFlipRequiresDecFlip = B(mount, "CalFlipRequiresDecFlip"),
                VariableDelayEnabled = B(delay, "Enabled"),
                VariableDelayShortS = D(delay, "ShortDelaySeconds"),
                VariableDelayLongS = D(delay, "LongDelaySeconds"),
                DitherScaleFactor = D(dither, "ScaleFactor"),
                DitherRaOnly = B(dither, "RaOnly"),
                RaAlgorithm = raAlgo?.Type == JTokenType.String ? raAlgo.Value<string>() : null,
                DecAlgorithm = decAlgo?.Type == JTokenType.String ? decAlgo.Value<string>() : null,
            };
        }

        /// <summary>The algorithm names the guider offers on an axis (<c>get_algos</c>), for Ara's pickers;
        /// empty when it does not answer.</summary>
        public async Task<IReadOnlyList<string>> GetAlgorithmsAsync(string axis, CancellationToken ct) {
            if (!Connected) {
                return Array.Empty<string>();
            }
            ct.ThrowIfCancellationRequested();
            var resp = await SendMessage<GenericPhdMethodResponse>(
                new Phd2NamedGetter("get_algos", new() { ["axis"] = axis }), LiveRpcTimeoutMs).ConfigureAwait(false);
            if (resp.error != null || resp.result is null) {
                return Array.Empty<string>();
            }
            return JToken.FromObject(resp.result) is JArray arr ? arr.Values<string>().OfType<string>().ToList() : Array.Empty<string>();
        }
    }
}
