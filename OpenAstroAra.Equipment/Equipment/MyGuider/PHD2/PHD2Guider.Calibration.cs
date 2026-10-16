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
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Equipment.Equipment.MyGuider.PHD2 {

    /// <summary>The guider's stored mount calibration (<c>get_calibration_data</c>): the RA and Dec
    /// axis angles on the camera (degrees), the guide rates (px/s), the parities and the declination it
    /// was taken at. <c>Calibrated</c> false means the rest is stale or absent.</summary>
    public sealed record GuiderCalibrationData(
        bool Calibrated,
        double? XAngleDeg,
        double? YAngleDeg,
        double? XRatePxPerSec,
        double? YRatePxPerSec,
        string? XParity,
        string? YParity,
        double? DeclinationDeg);

    public partial class PHD2Guider {

        /// <summary>#1364 — the stored calibration, or null when the guider is not connected or did not
        /// answer. <c>get_calibration_data</c> reports rates in px/ms × 1000 (px/s) and angles in degrees.</summary>
        public async Task<GuiderCalibrationData?> GetCalibrationDataAsync(CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            if (!Connected) {
                return null;
            }
            // The calibration view polls this once a second; a guider without a mount defined refuses it,
            // which is expected and handled here — never an inbox Error (#1366's lesson).
            var resp = await SendMessage<GenericPhdMethodResponse>(new Phd2GetCalibrationData(), LiveRpcTimeoutMs, notifyOnError: false).ConfigureAwait(false);
            if (resp?.error != null || resp?.result is not JObject cal) {
                return null;
            }
            static double? Num(JObject o, string key) =>
                o[key] is JToken t && t.Type is JTokenType.Float or JTokenType.Integer && double.IsFinite(t.Value<double>()) ? t.Value<double>() : null;
            return new GuiderCalibrationData(
                Calibrated: cal["calibrated"]?.Type == JTokenType.Boolean && cal["calibrated"]!.Value<bool>(),
                XAngleDeg: Num(cal, "xAngle"),
                YAngleDeg: Num(cal, "yAngle"),
                XRatePxPerSec: Num(cal, "xRate"),
                YRatePxPerSec: Num(cal, "yRate"),
                XParity: cal["xParity"]?.ToString(),
                YParity: cal["yParity"]?.ToString(),
                DeclinationDeg: Num(cal, "declination"));
        }

        /// <summary>Drop the stored calibration so the next guide start calibrates again.</summary>
        public async Task ClearCalibrationAsync(CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            RequireConnectedForLive();
            var response = await SendMessage<GenericPhdMethodResponse>(
                new Phd2ClearCalibration { Parameters = new object[] { "Mount" } }, LiveRpcTimeoutMs).ConfigureAwait(false);
            ThrowOnError("clear_calibration", response);
            ResetCalibrationRun();
        }
    }
}
