#region "copyright"

/* Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors. */

#endregion

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace OpenAstroAra.Core.Guiding;

public static class GuidingCalibrationAnalyzer {
    public static GuidingCalibrationQuality Analyze(string? calibrationJson) {
        var reasons = new List<string>();
        if (string.IsNullOrWhiteSpace(calibrationJson) || calibrationJson.Trim() == "null") {
            reasons.Add("PHD2 returned no calibration data.");
            return Invalid(null, null, null, null, null, reasons);
        }
        try {
            using var document = JsonDocument.Parse(calibrationJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) {
                reasons.Add("PHD2 calibration data is not an object.");
                return Invalid(null, null, null, null, null, reasons);
            }
            var root = document.RootElement;
            var raRate = Number(root, "xRate", "raRate", "rightAscensionRate");
            var decRate = Number(root, "yRate", "decRate", "declinationRate");
            var raAngle = Number(root, "xAngle", "raAngle", "rightAscensionAngle");
            var decAngle = Number(root, "yAngle", "decAngle", "declinationAngle");
            var dec = Number(root, "declination", "calibrationDeclination");
            var raParity = Text(root, "xParity", "raParity", "rightAscensionParity");
            var decParity = Text(root, "yParity", "decParity", "declinationParity");
            if (raRate is not > 0 || !double.IsFinite(raRate.Value)) reasons.Add("RA calibration rate is missing or invalid.");
            if (decRate is not > 0 || !double.IsFinite(decRate.Value)) reasons.Add("DEC calibration rate is missing or invalid.");
            if (raAngle is null || decAngle is null) reasons.Add("Calibration axis angles are missing.");
            if (string.IsNullOrWhiteSpace(raParity) || string.IsNullOrWhiteSpace(decParity)) reasons.Add("Calibration parity is missing.");

            double? orthogonalityError = null;
            if (raAngle is { } ra && decAngle is { } decAxis) {
                var raDegrees = Math.Abs(ra) <= 2 * Math.PI ? ra * 180 / Math.PI : ra;
                var decDegrees = Math.Abs(decAxis) <= 2 * Math.PI ? decAxis * 180 / Math.PI : decAxis;
                var separation = Math.Abs(((decDegrees - raDegrees + 540) % 360) - 180);
                orthogonalityError = Math.Abs(separation - 90);
                if (orthogonalityError > 25) reasons.Add("Calibration axes are not sufficiently orthogonal.");
            }
            return new GuidingCalibrationQuality(reasons.Count == 0, raRate, decRate,
                orthogonalityError, dec, raParity, decParity, reasons);
        } catch (JsonException) {
            reasons.Add("PHD2 calibration data is malformed JSON.");
            return Invalid(null, null, null, null, null, reasons);
        }
    }

    private static GuidingCalibrationQuality Invalid(double? raRate, double? decRate,
        double? orthogonality, double? declination, string? raParity, IReadOnlyList<string> reasons) =>
        new(false, raRate, decRate, orthogonality, declination, raParity, null, reasons);

    private static double? Number(JsonElement root, params string[] names) {
        foreach (var name in names)
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetDouble(out var number) && double.IsFinite(number)) return number;
        return null;
    }

    private static string? Text(JsonElement root, params string[] names) {
        foreach (var name in names)
            if (root.TryGetProperty(name, out var value)) {
                if (value.ValueKind == JsonValueKind.String) return value.GetString();
                if (value.ValueKind == JsonValueKind.Number) return value.GetRawText();
            }
        return null;
    }
}
