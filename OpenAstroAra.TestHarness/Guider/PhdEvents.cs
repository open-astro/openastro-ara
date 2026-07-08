#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Text.Json.Nodes;

namespace OpenAstroAra.TestHarness.Guider;

/// <summary>
/// Builders for the PHD2 event-server notifications a guider pushes over the wire
/// (the <c>{"Event":...}</c> messages the ARA client's listener consumes). Each
/// returns a <see cref="JsonObject"/> the <see cref="FakeGuider"/> serializes and
/// frames with a newline. Only the fields the ARA client reads are populated; the
/// timestamp/host/inst envelope fields are filled with stable placeholders.
/// </summary>
public static class PhdEvents {
    private static JsonObject Base(string @event) => new() {
        ["Event"] = @event,
        ["Timestamp"] = 0.0,
        ["Host"] = "fake-guider",
        ["Inst"] = 1,
    };

    /// <summary>The greeting PHD2 sends on connect.</summary>
    public static JsonObject Version(string phdVersion = "2.6.11", string subver = "ara-fake") {
        var e = Base("Version");
        e["PHDVersion"] = phdVersion;
        e["PHDSubver"] = subver;
        e["OverlapSupport"] = true;
        e["MsgVersion"] = 1;
        return e;
    }

    /// <summary>An <c>AppState</c> notification — e.g. Stopped, Looping, Guiding, Calibrating, LostLock.</summary>
    public static JsonObject AppState(string state) {
        var e = Base("AppState");
        e["State"] = state;
        return e;
    }

    /// <summary>§42.2 structured device-fault event (openastro-guider #57) — snake_case fields.</summary>
    public static JsonObject EquipmentDisconnected(string deviceType = "camera", string reason = "USB device removed", bool reconnecting = true) {
        var e = Base("EquipmentDisconnected");
        e["device_type"] = deviceType;
        e["reason"] = reason;
        e["reconnecting"] = reconnecting;
        return e;
    }

    /// <summary>§42.2 structured device-reconnect event (openastro-guider #57).</summary>
    public static JsonObject EquipmentReconnected(string deviceType = "camera") {
        var e = Base("EquipmentReconnected");
        e["device_type"] = deviceType;
        return e;
    }

    /// <summary>Guiding started (after calibration / on resume).</summary>
    public static JsonObject StartGuiding() => Base("StartGuiding");

    /// <summary>Calibration started.</summary>
    public static JsonObject StartCalibration(string mount = "Mount") {
        var e = Base("StartCalibration");
        e["Mount"] = mount;
        return e;
    }

    /// <summary>Calibration finished successfully.</summary>
    public static JsonObject CalibrationComplete(string mount = "Mount") {
        var e = Base("CalibrationComplete");
        e["Mount"] = mount;
        return e;
    }

    /// <summary>
    /// A single guide step. <paramref name="raDistanceRaw"/>/<paramref name="decDistanceRaw"/>
    /// are the RA/Dec raw distances (what the ARA client folds into guiding RMS);
    /// <paramref name="dx"/>/<paramref name="dy"/> are the pixel offsets (default 0 —
    /// pass them when a test needs pixel-vs-arcsec separation).
    /// </summary>
    public static JsonObject GuideStep(double raDistanceRaw, double decDistanceRaw, double dx = 0, double dy = 0) {
        var e = Base("GuideStep");
        e["dx"] = dx;
        e["dy"] = dy;
        e["RADistanceRaw"] = raDistanceRaw;
        e["DECDistanceRaw"] = decDistanceRaw;
        return e;
    }

    /// <summary>The guide star was lost (no longer trackable).</summary>
    public static JsonObject StarLost(int frame = 1, string status = "no star found") {
        var e = Base("StarLost");
        e["Frame"] = frame;
        e["StarMass"] = 0.0;
        e["SNR"] = 0.0;
        e["Status"] = status; // PHD2's StarLost.Status is a string (e.g. "low SNR")
        return e;
    }

    /// <summary>Settle progress during a dither/guide settle.</summary>
    public static JsonObject Settling(double distance, double time, double settleTime) {
        var e = Base("Settling");
        e["Distance"] = distance;
        e["Time"] = time;
        e["SettleTime"] = settleTime;
        return e;
    }

    /// <summary>Settle finished — <paramref name="status"/> 0 = success, non-zero = failed.</summary>
    public static JsonObject SettleDone(int status = 0, string? error = null) {
        var e = Base("SettleDone");
        e["Status"] = status;
        if (error is not null) {
            e["Error"] = error;
        }
        return e;
    }

    /// <summary>Guiding paused.</summary>
    public static JsonObject Paused() => Base("Paused");

    /// <summary>Guiding resumed.</summary>
    public static JsonObject Resumed() => Base("Resumed");

    /// <summary>A dither settle completed and guiding resumed.</summary>
    public static JsonObject GuidingDithered(double dx, double dy) {
        var e = Base("GuidingDithered");
        e["dx"] = dx;
        e["dy"] = dy;
        return e;
    }
}
