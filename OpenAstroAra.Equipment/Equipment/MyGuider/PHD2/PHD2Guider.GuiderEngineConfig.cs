#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Utility;
using OpenAstroAra.Profile.Interfaces;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Equipment.Equipment.MyGuider.PHD2 {

    /// <summary>
    /// §63.5 (guider-e-2) — pushes ARA's guider-engine config to the PHD2 daemon on connect, mapping the
    /// profile's <see cref="IGuiderSettings"/> onto the named-object RPCs (<c>set_profile_setup</c> /
    /// <c>set_algo_param</c> / <c>set_dec_guide_mode</c>). ARA owns these values (§63.5); this is where they
    /// reach the guider so a fresh PHD2 profile picks up the user's scope/camera + guiding aggressiveness.
    /// </summary>
    public sealed partial class PHD2Guider {

        /// <summary>
        /// Push the §63.5 guider-engine config. <c>set_profile_setup</c> requires the PHD2 equipment to be
        /// disconnected, so this runs in the disconnected window <em>before</em> the connect path's
        /// <c>EnsurePHD2EquipmentConnected</c> reconnects it. Every message is best-effort: a rejected RPC
        /// (e.g. an algo-param name a given PHD2 build doesn't expose) is logged and skipped so the push can
        /// never block — or fail — the connect itself.
        /// </summary>
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Best-effort §63.5 push boundary: each setter RPC may throw (socket drop, a PHD2 build that doesn't expose a param) — it's logged and skipped so a config push can never fail or block the guider connect itself.")]
        private async Task PushGuiderEngineConfigAsync(CancellationToken ct) {
            var guider = profileService.ActiveProfile.GuiderSettings;
            var messages = BuildGuiderEngineConfigMessages(guider);

            if (messages.Count == 0) {
                // All values left at their unset sentinels (0 / "auto") — nothing to push. Logged so an
                // operator who expected their scope/aggressiveness to reach the guider can see the push ran
                // but found nothing configured, rather than wondering whether it fired at all.
                Logger.Debug("PHD2 §63.5 push - no guider-engine config set (all values unset/Auto); leaving the daemon's own settings.");
                return;
            }

            // "Auto" dec-mode is intentionally skipped (it's PHD2's own default); surfacing its absence keeps a
            // missing set_dec_guide_mode from reading as a bug. Inferred from the built message set (the single
            // source of truth — BuildGuiderEngineConfigMessages emits one iff dec-mode != Auto) so this can't
            // drift from the builder's own skip rule.
            if (!messages.OfType<Phd2SetDecGuideMode>().Any()) {
                Logger.Debug("PHD2 §63.5 push - dec-guide-mode is Auto (PHD2's default); not pushed, leaving the daemon's own setting.");
            }

            // Only set_profile_setup and the §63.17 selection setters need the equipment off, so only pay the
            // disconnect → reconnect cost when one is actually being sent — otherwise the algo-param /
            // dec-mode pushes apply at runtime and we leave an already-connected (possibly calibrated)
            // session alone.
            var disconnectedForSetup = messages.Any(RequiresDisconnectedEquipment);
            if (disconnectedForSetup) {
                // Best-effort like the sends: a socket drop here must not propagate into Connect's catch
                // (user-visible error + aborted connect). Log + proceed — set_profile_setup will then just fail
                // its own best-effort send if the equipment is still connected.
                try {
                    await DisconnectPHD2Equipment();
                } catch (OperationCanceledException) {
                    throw;
                } catch (Exception ex) {
                    Logger.Warning($"PHD2 §63.5 push - equipment disconnect for set_profile_setup failed: {ex.Message}");
                }
            }

            var applied = 0;
            foreach (var msg in messages) {
                ct.ThrowIfCancellationRequested();
                try {
                    var resp = await SendMessage(msg);
                    if (resp?.error != null) {
                        // SendMessage synthesizes an error on socket failure too (code -1), so this covers both
                        // a true PHD2 rejection and a transport failure — "not applied" reads correctly for both.
                        Logger.Warning($"PHD2 §63.5 push - {msg.Method} not applied: {resp.error}");
                    } else {
                        applied++;
                    }
                } catch (OperationCanceledException) {
                    throw; // a cancelled Connect must stop the push, not swallow it as a per-message failure
                } catch (Exception ex) {
                    Logger.Warning($"PHD2 §63.5 push - {msg.Method} failed: {ex.Message}");
                }
            }

            // Summary so the connect log shows what guider-engine config reached the daemon at a glance — the
            // per-message lines above only appear on failure, so without this a successful push is silent.
            // Report applied-vs-attempted (not just the count) so a partial failure is visible at Info level
            // without scanning for the Warning lines, and escalate to Warning when any message didn't land.
            var setup = disconnectedForSetup ? " (equipment disconnected for set_profile_setup)" : string.Empty;
            if (applied == messages.Count) {
                Logger.Info($"PHD2 §63.5 push - applied all {messages.Count} guider-engine setting message(s){setup}.");
            } else {
                Logger.Warning($"PHD2 §63.5 push - applied {applied} of {messages.Count} guider-engine setting message(s); {messages.Count - applied} did not land (see warnings above){setup}.");
            }
        }

        /// <summary>
        /// Pure mapping from <see cref="IGuiderSettings"/> to the ordered PHD2 setter messages. Focal length /
        /// pixel size are only pushed when configured (&gt; 0). Aggressiveness is sent as the 0..1 fraction
        /// <c>set_algo_param</c> expects (ARA already stores it that way); minimum-move goes to both axes.
        /// Socket-free, so the mapping is unit-testable without a live PHD2.
        /// </summary>
        public static IReadOnlyList<Phd2Method> BuildGuiderEngineConfigMessages(IGuiderSettings guider) {
            var messages = new List<Phd2Method>();

            // §63.17 equipment selection — ordered FIRST (the daemon's own integration flow selects devices
            // before profile setup). Every selection treats ""/whitespace as "unset, keep the daemon's own"
            // (same sentinel convention as the numeric 0s below); values are the daemon's choice strings
            // verbatim. All of these are blocked while equipment is connected, so they ride the same
            // disconnected window as set_profile_setup (see RequiresDisconnectedEquipment).
            if (!string.IsNullOrWhiteSpace(guider.GuiderAlpacaHost) || guider.GuiderAlpacaPort > 0) {
                messages.Add(new Phd2SetAlpacaServer {
                    Parameters = new Phd2SetAlpacaServerParameter {
                        Host = string.IsNullOrWhiteSpace(guider.GuiderAlpacaHost) ? null : guider.GuiderAlpacaHost.Trim(),
                        Port = guider.GuiderAlpacaPort > 0 ? guider.GuiderAlpacaPort : null,
                    },
                });
            }
            if (!string.IsNullOrWhiteSpace(guider.GuiderCamera)) {
                messages.Add(new Phd2SetSelectedCamera { Parameters = new() { Camera = guider.GuiderCamera.Trim() } });
            }
            if (!string.IsNullOrWhiteSpace(guider.GuiderCameraId)) {
                messages.Add(new Phd2SetSelectedCameraId { Parameters = new() { CameraId = guider.GuiderCameraId.Trim() } });
            }
            if (!string.IsNullOrWhiteSpace(guider.GuiderMount)) {
                messages.Add(new Phd2SetSelectedMount { Parameters = new() { Mount = guider.GuiderMount.Trim() } });
            }
            if (!string.IsNullOrWhiteSpace(guider.GuiderAuxMount)) {
                messages.Add(new Phd2SetSelectedAuxMount { Parameters = new() { AuxMount = guider.GuiderAuxMount.Trim() } });
            }
            if (!string.IsNullOrWhiteSpace(guider.GuiderRotator)) {
                messages.Add(new Phd2SetSelectedRotator { Parameters = new() { Rotator = guider.GuiderRotator.Trim() } });
            }

            var setup = new Phd2ProfileSetupParameter();
            if (guider.GuideFocalLength > 0) {
                setup.FocalLength = guider.GuideFocalLength;
            }
            if (guider.GuidePixelSize > 0) {
                setup.PixelSize = guider.GuidePixelSize;
            }
            if (setup.FocalLength != null || setup.PixelSize != null) {
                messages.Add(new Phd2SetProfileSetup { Parameters = setup });
            }

            // Every numeric param treats 0 as "unset" and is skipped: pushing 0 would overwrite PHD2's own
            // sensible value with a harmful one — aggressiveness 0 disables corrections, minMove 0 makes the
            // mount chase noise (PHD2 defaults ~0.2px). Profiles default to non-zero (0.7 / 0.15), so this only
            // skips an explicit/leaked 0, leaving PHD2's value in that edge case.
            if (guider.RAAggressiveness > 0) {
                messages.Add(AlgoParam("ra", "aggressiveness", guider.RAAggressiveness));
            }
            if (guider.DecAggressiveness > 0) {
                messages.Add(AlgoParam("dec", "aggressiveness", guider.DecAggressiveness));
            }
            if (guider.MinimumMove > 0) {
                messages.Add(AlgoParam("ra", "minMove", guider.MinimumMove));
                messages.Add(AlgoParam("dec", "minMove", guider.MinimumMove));
            }

            // dec-guide-mode: "Auto" is both ARA's default and PHD2's own default, so treat it as the unset
            // sentinel (like the numeric 0s) and don't push it — otherwise a fresh ARA profile would overwrite
            // a user's deliberate PHD2 North/South (e.g. a backlash-sensitive mount) on every connect. Only an
            // explicit North/South/Off is sent.
            var decMode = MapDecGuideMode(guider.DecGuideMode);
            if (decMode != "Auto") {
                messages.Add(new Phd2SetDecGuideMode { Parameters = new() { Mode = decMode } });
            }
            return messages;
        }

        /// <summary>§63.17 — the daemon blocks these RPCs while equipment is connected (doc/jsonrpc_api.md),
        /// so their presence in a push forces the disconnect → reconnect window. Single source of truth for
        /// the push's disconnect decision; unit-tested so a new selection setter can't silently skip it.</summary>
        public static bool RequiresDisconnectedEquipment(Phd2Method message) => message
            is Phd2SetProfileSetup
            or Phd2SetAlpacaServer
            or Phd2SetSelectedCamera
            or Phd2SetSelectedCameraId
            or Phd2SetSelectedMount
            or Phd2SetSelectedAuxMount
            or Phd2SetSelectedRotator;

        /// <summary>
        /// §63.17 — on-demand re-push of the §63.5 engine config + equipment selections (the connect path
        /// pushes automatically; this serves POST /guider/profile/push so a settings edit takes effect without
        /// a full reconnect). Runs the same best-effort push — including its disconnect window when a
        /// selection/setup message is queued — then re-ensures the daemon's equipment is connected. Returns
        /// the RPC method names attempted, for the <c>guider.profile_pushed</c> event's fields-changed payload.
        /// Requires a connected guider (throws <see cref="InvalidOperationException"/>).
        /// </summary>
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Catch-and-rethrow-typed boundary: a post-push equipment reconnect failure (transport drop, daemon contract break) is converted to GuiderRpcException so the service layer maps it to an actionable 422 instead of a raw 500. OperationCanceledException is rethrown untouched.")]
        public async Task<IReadOnlyList<string>> RepushGuiderEngineConfigAsync(CancellationToken ct) {
            if (!Connected) {
                throw new InvalidOperationException("guider is not connected");
            }
            var pushed = BuildGuiderEngineConfigMessages(profileService.ActiveProfile.GuiderSettings)
                .Select(m => m.Method).Distinct().ToList();
            await PushGuiderEngineConfigAsync(ct);
            // The push only reconnects equipment when it opened the disconnect window; make the post-push
            // state deterministic for the caller either way. A reconnect failure here is a real, actionable
            // fault — the realistic case being a just-pushed selection the daemon can't connect (wrong
            // camera/mount choice) — and the push must NOT read as success while the daemon's equipment is
            // left off. EnsurePHD2EquipmentConnected reports failure by RETURNING FALSE (SendMessage swallows
            // transport errors into synthetic error responses, so it effectively never throws): check the
            // bool and surface a typed RPC error (→ 422 at the endpoint). The catch stays as a belt for a
            // daemon contract break (e.g. a non-boolean get_connected result throws InvalidCastException).
            bool equipmentConnected;
            try {
                equipmentConnected = await EnsurePHD2EquipmentConnected();
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                throw new GuiderRpcException("set_connected", -1,
                    $"profile pushed, but reconnecting the guider's equipment failed: {ex.Message}");
            }
            if (!equipmentConnected) {
                throw new GuiderRpcException("set_connected", -1,
                    "profile pushed, but the daemon could not reconnect its equipment — check that the "
                    + "selected camera/mount/rotator are reachable (get_connected/set_connected failed).");
            }
            return pushed;
        }

        private static Phd2SetAlgoParam AlgoParam(string axis, string name, double value) =>
            new() { Parameters = new Phd2SetAlgoParamParameter { Axis = axis, Name = name, Value = value } };

        /// <summary>
        /// ARA stores the dec-guide-mode lowercase ({auto,north,south,off}); PHD2's <c>set_dec_guide_mode</c>
        /// expects the capitalized {Auto,North,South,Off} (per the guider API reference). Unknown ⇒ Auto.
        /// </summary>
        public static string MapDecGuideMode(string? araMode) => araMode?.ToUpperInvariant() switch {
            "NORTH" => "North",
            "SOUTH" => "South",
            "OFF" => "Off",
            _ => "Auto",
        };
    }
}
