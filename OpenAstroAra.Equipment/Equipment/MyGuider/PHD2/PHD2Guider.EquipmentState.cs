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
using OpenAstroAra.Core.Utility;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Equipment.Equipment.MyGuider.PHD2 {

    /// <summary>#2074 — the Alpaca server the guider holds (<c>get_alpaca_server</c>). A field the guider did
    /// not report is null.</summary>
    public sealed record GuiderAlpacaServerState(string? Host, int? Port, int? CameraDevice, int? TelescopeDevice, int? RotatorDevice);

    /// <summary>#2074 — what the guider holds before a §63.5 push: its equipment selections, its Alpaca
    /// server, the profile-setup values Ara pushes, and whether it is calibrated. A field the guider refused
    /// or answered with the wrong type is null, and a null always compares as "changed".</summary>
    public sealed record GuiderEquipmentState(
        string? Camera,
        string? CameraId,
        string? Mount,
        string? Rotator,
        GuiderAlpacaServerState? Alpaca,
        int? FocalLength,
        double? PixelSize,
        bool? MultiStar,
        bool? Calibrated);

    /// <summary>#2074 — what a §63.5 push sends after it is compared with the guider's state.
    /// <see cref="SelectionsSkipped"/> and <see cref="SetupSkipped"/> say which unchanged groups were left
    /// out. <see cref="RestoreCalibrationAfterReconnect"/> is true when the push opens the disconnect
    /// window, the guider was calibrated, and the gear and optics are unchanged: the calibration is still
    /// valid, so it is put back if the reconnect lost it.</summary>
    public sealed record GuiderEnginePushPlan(
        IReadOnlyList<Phd2Method> Messages,
        bool SelectionsSkipped,
        bool SetupSkipped,
        bool RestoreCalibrationAfterReconnect) {

        /// <summary>True when a message that needs the guider's equipment off is still in the plan.</summary>
        public bool OpensDisconnectWindow => Messages.Any(PHD2Guider.RequiresDisconnectedEquipment);
    }

    public sealed partial class PHD2Guider {

        /// <summary>The guider prints <c>pixel_size</c> with <c>%g</c> (six significant digits, guider
        /// event_server.cpp <c>NV(name, double)</c>), so a value Ara holds to more digits (an Alpaca driver's
        /// float, e.g. 3.7599999904632568) reads back rounded. Compared relative to that rounding, plus a
        /// tiny absolute floor.</summary>
        private const double PixelSizeRelativeTolerance = 1e-5;
        private const double PixelSizeAbsoluteTolerance = 1e-9;

        /// <summary>#2074 — the equipment selection group of a push: the Alpaca server and every
        /// <c>set_selected_*</c> setter. The group is sent whole or not at all, so its order (Alpaca server
        /// first, §63.20) never breaks.</summary>
        public static bool IsEquipmentSelectionMessage(Phd2Method message) => message
            is Phd2SetAlpacaServer
            or Phd2SetSelectedCamera
            or Phd2SetSelectedCameraId
            or Phd2SetSelectedMount
            or Phd2SetSelectedAuxMount
            or Phd2SetSelectedRotator;

        /// <summary>#2074 — true when every selection message in <paramref name="messages"/> already matches
        /// the guider's state, so sending it again would only rebuild the guider's device objects (and drop its
        /// calibration). A list with no selection message is unchanged. Pure.
        /// <para>Choice strings compare trimmed and case-insensitive, like the daemon's own match, which
        /// stores the canonical choice. A request the daemon resolves by a fuzzy rule (a bare
        /// <c>"Alpaca Camera"</c>, an <c>"INDI Camera"</c> without the driver) never equals the stored
        /// choice, so it reads as changed and goes out as before. The aux mount is not compared: this guider
        /// refuses <c>set_selected_aux_mount</c> always, so it rides with the group and never decides it.</para></summary>
        public static bool SelectionsUnchanged(IEnumerable<Phd2Method> messages, GuiderEquipmentState? state) {
            ArgumentNullException.ThrowIfNull(messages);
            foreach (var message in messages) {
                var same = message switch {
                    Phd2SetAlpacaServer { Parameters: var p } => AlpacaServerUnchanged(p, state?.Alpaca),
                    Phd2SetSelectedCamera { Parameters: var p } => SameChoice(p?.Camera, state?.Camera),
                    // The id is a serial number or a driver key: compared exactly, never case-folded.
                    Phd2SetSelectedCameraId { Parameters: var p } =>
                        p?.CameraId is { } id && state?.CameraId is { } held && string.Equals(id.Trim(), held.Trim(), StringComparison.Ordinal),
                    Phd2SetSelectedMount { Parameters: var p } => SameChoice(p?.Mount, state?.Mount),
                    Phd2SetSelectedRotator { Parameters: var p } => SameChoice(p?.Rotator, state?.Rotator),
                    _ => true,
                };
                if (!same) {
                    return false;
                }
            }
            return true;
        }

        /// <summary>#2074 — true when every <c>set_profile_setup</c> in <paramref name="messages"/> already
        /// matches the guider (focal length, pixel size within the guider's <c>%g</c> rounding, multi-star). With
        /// <paramref name="ignoreMultiStar"/> only the optics are compared. A field Ara does not read back
        /// from the guider counts as changed. Pure.</summary>
        public static bool SetupUnchanged(IEnumerable<Phd2Method> messages, GuiderEquipmentState? state, bool ignoreMultiStar = false) {
            ArgumentNullException.ThrowIfNull(messages);
            foreach (var setup in messages.OfType<Phd2SetProfileSetup>()) {
                var p = setup.Parameters;
                if (p is null) {
                    continue;
                }
                if (p.CameraBinning is not null || p.GuideSpeed is not null
                    || p.CalibrationDuration is not null || p.CalibrationDistance is not null) {
                    return false;
                }
                if (p.FocalLength is { } focal && state?.FocalLength != focal) {
                    return false;
                }
                if (p.PixelSize is { } pixel
                    && (state?.PixelSize is not { } heldPixel || !SamePixelSize(heldPixel, pixel))) {
                    return false;
                }
                if (!ignoreMultiStar && p.MultiStarEnabled is { } multi && state?.MultiStar != multi) {
                    return false;
                }
            }
            return true;
        }

        /// <summary>#2074 — compare a built §63.5 message list with the guider's state and leave out what
        /// would not change. The selection group is all-or-nothing; <c>set_profile_setup</c> is dropped on
        /// its own. Only a message left in the plan that needs the equipment off opens the disconnect window,
        /// so a multi-star-only change sends <c>set_profile_setup</c> alone, which does not rebuild the
        /// guider's mount. A null <paramref name="state"/> (an older guider, or no answer) keeps every
        /// message, as before. Pure.</summary>
        public static GuiderEnginePushPlan PlanEngineConfigPush(IReadOnlyList<Phd2Method> messages, GuiderEquipmentState? state) {
            ArgumentNullException.ThrowIfNull(messages);
            var selectionsUnchanged = SelectionsUnchanged(messages, state);
            var skipSelections = messages.Any(IsEquipmentSelectionMessage) && selectionsUnchanged;
            var skipSetup = messages.Any(m => m is Phd2SetProfileSetup) && SetupUnchanged(messages, state);
            var send = messages
                .Where(m => !(skipSelections && IsEquipmentSelectionMessage(m)) && !(skipSetup && m is Phd2SetProfileSetup))
                .ToList();
            var opensWindow = send.Any(RequiresDisconnectedEquipment);
            var restore = opensWindow
                && state?.Calibrated == true
                && selectionsUnchanged
                && SetupUnchanged(messages, state, ignoreMultiStar: true);
            return new GuiderEnginePushPlan(send, skipSelections, skipSetup, restore);
        }

        private static bool SamePixelSize(double held, double pushed) =>
            Math.Abs(held - pushed) <= Math.Max(Math.Abs(held), Math.Abs(pushed)) * PixelSizeRelativeTolerance + PixelSizeAbsoluteTolerance;

        private static bool SameChoice(string? pushed, string? held) =>
            pushed is not null && held is not null
            && string.Equals(pushed.Trim(), held.Trim(), StringComparison.OrdinalIgnoreCase);

        private static bool AlpacaServerUnchanged(Phd2SetAlpacaServerParameter? pushed, GuiderAlpacaServerState? held) {
            if (pushed is null) {
                return true;
            }
            if (held is null) {
                return false;
            }
            if (pushed.Host is { } host
                && (held.Host is not { } heldHost || !string.Equals(host.Trim(), heldHost.Trim(), StringComparison.OrdinalIgnoreCase))) {
                return false;
            }
            return Same(pushed.Port, held.Port)
                && Same(pushed.CameraDevice, held.CameraDevice)
                && Same(pushed.TelescopeDevice, held.TelescopeDevice)
                && Same(pushed.RotatorDevice, held.RotatorDevice);

            // A field the push leaves out is not compared; one it sends must equal what the guider holds.
            static bool Same(int? sent, int? have) => sent is null || sent == have;
        }

        /// <summary>#2074 — read what the guider holds before a push. The getters run in parallel and each is
        /// best-effort: a refusal, a timeout or a wrong-typed answer leaves its field null, which the plan
        /// reads as "changed".</summary>
        private async Task<GuiderEquipmentState> ReadGuiderEquipmentStateAsync(CancellationToken ct) {
            async Task<JToken?> Get(string method) {
                ct.ThrowIfCancellationRequested();
                var resp = await SendMessage<GenericPhdMethodResponse>(new Phd2NamedGetter(method), LiveRpcTimeoutMs, notifyOnError: false).ConfigureAwait(false);
                return resp.error != null || resp.result is null ? null : JToken.FromObject(resp.result);
            }
            static string? S(JToken? t) => t?.Type == JTokenType.String ? t.Value<string>() : null;
            static bool? B(JToken? t) => t?.Type == JTokenType.Boolean ? t.Value<bool>() : null;
            static double? D(JToken? t) => t?.Type is JTokenType.Float or JTokenType.Integer ? t.Value<double>() : null;
            static int? I(JToken? t) => t?.Type == JTokenType.Integer ? t.Value<int>() : null;

            var cameraT = Get("get_selected_camera");
            var cameraIdT = Get("get_selected_camera_id");
            var mountT = Get("get_selected_mount");
            var rotatorT = Get("get_selected_rotator");
            var alpacaT = Get("get_alpaca_server");
            var setupT = Get("get_profile_setup");
            var calibratedT = Get("get_calibrated");
            await Task.WhenAll(cameraT, cameraIdT, mountT, rotatorT, alpacaT, setupT, calibratedT).ConfigureAwait(false);

            var alpaca = await alpacaT.ConfigureAwait(false) as JObject;
            var setup = await setupT.ConfigureAwait(false) as JObject;
            return new GuiderEquipmentState(
                Camera: S(await cameraT.ConfigureAwait(false)),
                CameraId: S(await cameraIdT.ConfigureAwait(false)),
                Mount: S(await mountT.ConfigureAwait(false)),
                Rotator: S(await rotatorT.ConfigureAwait(false)),
                Alpaca: alpaca is null ? null : new GuiderAlpacaServerState(
                    S(alpaca["host"]), I(alpaca["port"]), I(alpaca["camera_device"]), I(alpaca["telescope_device"]), I(alpaca["rotator_device"])),
                FocalLength: I(setup?["focal_length"]),
                PixelSize: D(setup?["pixel_size"]),
                MultiStar: B(setup?["multistar_enabled"]),
                Calibrated: B(await calibratedT.ConfigureAwait(false)));
        }

        /// <summary>#2074 — after a push whose window opened over unchanged gear and optics, put back the
        /// calibration the reconnect lost. The guider keeps it across a plain disconnect, so this only acts
        /// when <c>get_calibrated</c> reads false after the reconnect (the guider cleared it on its own, for
        /// example after an earlier camera or pixel-size change in the same daemon run). Best-effort: it logs,
        /// and never fails the caller.</summary>
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Best-effort #2074 restore after a push: a socket drop or a refusal is logged; the caller's connect or push must not fail because the calibration could not be put back.")]
        private async Task RestoreCalibrationAfterPushAsync(CancellationToken ct) {
            try {
                var resp = await SendMessage<GenericPhdMethodResponse>(new Phd2GetCalibrated(), LiveRpcTimeoutMs, notifyOnError: false).ConfigureAwait(false);
                if (resp.error == null && resp.result is true) {
                    Logger.Debug("PHD2 §63.5 push - guider calibration kept across the equipment reconnect.");
                    return;
                }
                if (await RestoreCalibrationAsync(ct).ConfigureAwait(false)) {
                    Logger.Info("PHD2 §63.5 push - guider calibration restored after the equipment reconnect.");
                } else {
                    Logger.Warning("PHD2 §63.5 push - the equipment reconnect lost the guider calibration and the guider could not restore it; the next guide start calibrates.");
                }
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                Logger.Warning($"PHD2 §63.5 push - restoring the guider calibration after the reconnect failed: {ex.Message}");
            }
        }
    }
}
