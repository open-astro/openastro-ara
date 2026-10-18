#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Globalization;

namespace OpenAstroAra.Server.Services;

public enum GuiderCameraReconcileOutcome {
    /// <summary>Nothing to do or nothing safe to learn (equal values, unreadable camera, unknown slot).</summary>
    Unchanged,
    /// <summary>The profile takes <see cref="GuiderCameraReconcileDecision.NewPixelSize"/> and/or <see cref="GuiderCameraReconcileDecision.NewUniqueId"/>.</summary>
    Update,
    /// <summary>The slot's UniqueID differs from a non-empty pin: leave the profile alone, log it.</summary>
    PinMismatch,
}

public readonly record struct GuiderCameraReconcileDecision(GuiderCameraReconcileOutcome Outcome, double? NewPixelSize, string? NewUniqueId);

/// <summary>
/// §63.20 pixel-size autofill, re-run on connect (#1362): when the guider's equipment connects, the guide
/// camera's Alpaca <c>PixelSizeX</c> and <c>UniqueID</c> are compared with the profile's
/// <c>guide_pixel_size</c> / <c>guider_camera_alpaca_unique_id</c>. Pure: the caller reads the camera and
/// the server's <c>configureddevices</c> and applies the result.
/// <para>The #1308 pin wins: a non-empty pinned id that differs from the slot's UniqueID means the camera
/// moved or was swapped (<see cref="GuiderCameraPinCheck"/> refuses it on push); nothing is overwritten and
/// the pixel size is left alone because it may belong to the other camera. That outcome does not depend on
/// <c>readPixelSize</c>, so the caller decides it before reading the camera, and logs it even while the
/// main imaging camera is held.</para>
/// </summary>
public static class GuiderCameraReconcile {

    // Alpaca reports the size as a double; a rounding wobble must not rewrite the profile on every connect.
    private const double PixelSizeEpsilon = 1e-6;

    /// <summary>
    /// <paramref name="devices"/> is the server's <c>configureddevices</c> map, EMPTY when the lookup failed
    /// (the identity is then unknown, so a non-empty pin stays unchecked and untouched).
    /// <paramref name="readPixelSize"/> is null or non-positive when the camera could not say.
    /// </summary>
    public static GuiderCameraReconcileDecision Decide(string? pinnedUniqueId, double currentPixelSize, int deviceNumber,
            IReadOnlyDictionary<string, AlpacaConfiguredDevice> devices, double? readPixelSize) {
        ArgumentNullException.ThrowIfNull(devices);
        var pinned = (pinnedUniqueId ?? string.Empty).Trim();
        var slotKnown = devices.Count > 0;
        var slotId = string.Empty;
        if (slotKnown && devices.TryGetValue(SlotKey(deviceNumber), out var atSlot)) {
            slotId = (atSlot.UniqueId ?? string.Empty).Trim();
        }

        string? newId = null;
        if (pinned.Length == 0) {
            if (slotId.Length > 0) {
                newId = slotId;
            }
        } else if (!slotKnown) {
            return new GuiderCameraReconcileDecision(GuiderCameraReconcileOutcome.Unchanged, null, null);
        } else if (!string.Equals(slotId, pinned, StringComparison.Ordinal)) {
            return new GuiderCameraReconcileDecision(GuiderCameraReconcileOutcome.PinMismatch, null, null);
        }

        double? newPixel = readPixelSize is > 0 && Math.Abs(readPixelSize.Value - currentPixelSize) > PixelSizeEpsilon
            ? readPixelSize
            : null;
        return newId is null && newPixel is null
            ? new GuiderCameraReconcileDecision(GuiderCameraReconcileOutcome.Unchanged, null, null)
            : new GuiderCameraReconcileDecision(GuiderCameraReconcileOutcome.Update, newPixel, newId);
    }

    public static string SlotKey(int deviceNumber) =>
        $"camera/{deviceNumber.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Device text reaches the log: drop control characters (CWE-117) and bidi controls, which can
    /// reorder what a reader sees (CVE-2021-42574), and cap the length.</summary>
    public static string ForLog(string? value) {
        var clean = new string((value ?? string.Empty).Where(c => !char.IsControl(c) && !IsBidiControl(c)).ToArray());
        return clean.Length > 128 ? clean[..128] : clean;
    }

    // ALM, LRM/RLM, the embeddings and overrides (U+202A-202E) and the isolates (U+2066-2069).
    private static bool IsBidiControl(char c) =>
        c is '\u061C' or '\u200E' or '\u200F' or (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069');
}
