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

            // Only set_profile_setup (focal/pixel) needs the equipment off, so only pay the
            // disconnect → reconnect cost when one is actually being sent — otherwise the algo-param /
            // dec-mode pushes apply at runtime and we leave an already-connected (possibly calibrated)
            // session alone.
            if (messages.OfType<Phd2SetProfileSetup>().Any()) {
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

            foreach (var msg in messages) {
                ct.ThrowIfCancellationRequested();
                try {
                    var resp = await SendMessage(msg);
                    if (resp?.error != null) {
                        // SendMessage synthesizes an error on socket failure too (code -1), so this covers both
                        // a true PHD2 rejection and a transport failure — "not applied" reads correctly for both.
                        Logger.Warning($"PHD2 §63.5 push - {msg.Method} not applied: {resp.error}");
                    }
                } catch (OperationCanceledException) {
                    throw; // a cancelled Connect must stop the push, not swallow it as a per-message failure
                } catch (Exception ex) {
                    Logger.Warning($"PHD2 §63.5 push - {msg.Method} failed: {ex.Message}");
                }
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
