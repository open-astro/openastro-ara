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
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Equipment.Equipment.MyGuider.PHD2 {

    /// <summary>The §63.4 profile-selection decision (pure, so the connect path's choice is unit-testable).</summary>
    public enum AraProfileActionKind {
        /// <summary>Already on the right PHD2 profile — do nothing.</summary>
        None,
        /// <summary>Honor the user's explicit <c>PHD2ProfileId</c> override via the inherited id-based switch.</summary>
        SelectById,
        /// <summary>The <c>ara-&lt;slug&gt;</c> profile exists — select it by name.</summary>
        SelectByName,
        /// <summary>The <c>ara-&lt;slug&gt;</c> profile does not exist yet — create + select it.</summary>
        Create,
    }

    /// <summary>Resolved §63.4 selection: which action, plus the id (for <see cref="AraProfileActionKind.SelectById"/>)
    /// or the <c>ara-&lt;slug&gt;</c> name (for SelectByName / Create).</summary>
    public readonly record struct AraProfileSelection(AraProfileActionKind Kind, int Id, string Name);

    /// <summary>
    /// §63.4 (guider-e-3) — maps an ARA profile to its 1:1 PHD2 profile by name. ARA owns a dedicated
    /// PHD2 profile per ARA profile (<c>ara-&lt;slug&gt;</c>) so each rig keeps its own guider tuning,
    /// calibration, and dark library; switching ARA profiles selects (or, on first connect, creates) the
    /// matching PHD2 profile via <see cref="Phd2SetProfileByName"/> / <see cref="Phd2CreateProfile"/>.
    /// This file carries only the pure name mapping (guider-e-3a); the connect-path orchestration that
    /// drives the RPCs is guider-e-3b.
    /// </summary>
    public sealed partial class PHD2Guider {

        /// <summary>
        /// Derive the PHD2 profile name for an ARA profile per §63.4 (<c>ara-&lt;slug&gt;</c>). The slug is a
        /// deterministic lowercase a-z/0-9 form of the ARA profile name: runs of any other character collapse
        /// to a single hyphen, leading/trailing hyphens are trimmed. Examples (§63.4):
        /// <c>"C14 on CEM120" → "ara-c14-on-cem120"</c>, <c>"RedCat on HEQ5" → "ara-redcat-on-heq5"</c>. A
        /// name that slugs to empty (null / whitespace / all-punctuation / non-ASCII-only) falls back to
        /// <c>"ara-default"</c>. Non-ASCII letters are not transliterated — they collapse to hyphens like any
        /// other separator (the slug is an internal PHD2 identifier, not user-facing text).
        /// </summary>
        /// <remarks>
        /// Pure + socket-free so it's unit-testable without a live guider. Two ARA names that differ only in
        /// punctuation/case can slug to the same PHD2 name (e.g. <c>"C-14"</c> and <c>"C 14"</c> → <c>ara-c-14</c>);
        /// disambiguating that collision is deferred to the guider-e-3b wiring (tracked in PORT_TODO).
        /// </remarks>
        public static string AraGuiderProfileName(string? araProfileName) {
            var sb = new StringBuilder();
            var lastWasHyphen = true; // start true so leading separators don't emit a leading hyphen
            foreach (var ch in araProfileName ?? string.Empty) {
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')) {
                    sb.Append(ch);
                    lastWasHyphen = false;
                } else if (ch >= 'A' && ch <= 'Z') {
                    // Lowercase ASCII inline (avoids ToLowerInvariant / CA1308): the slug must be lowercase
                    // to match the §63.4 convention, and this is an identifier format, not a security fold.
                    sb.Append((char)(ch - 'A' + 'a'));
                    lastWasHyphen = false;
                } else if (!lastWasHyphen) {
                    sb.Append('-');
                    lastWasHyphen = true;
                }
            }
            if (sb.Length > 0 && sb[^1] == '-') {
                sb.Length--; // trim the trailing hyphen a separator-terminated name leaves behind
            }
            return sb.Length == 0 ? "ara-default" : "ara-" + sb.ToString();
        }

        /// <summary>
        /// Decide what profile action a guider connect should take (§63.4), pure + socket-free so the connect
        /// path's choice is unit-testable. Precedence: an explicit <paramref name="overrideProfileId"/> (the
        /// inherited <c>GuiderSettings.PHD2ProfileId</c> — the user's manual override) wins and selects by id;
        /// otherwise the ARA profile maps to its <c>ara-&lt;slug&gt;</c> PHD2 profile, selected by name if it
        /// already exists or created if not. When the target is already the selected profile, the result is
        /// <see cref="AraProfileActionKind.None"/> so connect doesn't needlessly drop the equipment.
        /// </summary>
        public static AraProfileSelection ResolveAraProfileSelection(
            int? overrideProfileId,
            int? selectedProfileId,
            string? activeAraProfileName,
            IReadOnlyList<Phd2Profile> availableProfiles) {

            if (overrideProfileId.HasValue) {
                return selectedProfileId == overrideProfileId.Value
                    ? new AraProfileSelection(AraProfileActionKind.None, 0, string.Empty)
                    : new AraProfileSelection(AraProfileActionKind.SelectById, overrideProfileId.Value, string.Empty);
            }

            var araName = AraGuiderProfileName(activeAraProfileName);
            var existing = availableProfiles.FirstOrDefault(p => string.Equals(p.Name, araName, StringComparison.Ordinal));
            if (existing != null) {
                return selectedProfileId == existing.Id
                    ? new AraProfileSelection(AraProfileActionKind.None, existing.Id, araName)
                    : new AraProfileSelection(AraProfileActionKind.SelectByName, existing.Id, araName);
            }
            return new AraProfileSelection(AraProfileActionKind.Create, 0, araName);
        }

        /// <summary>
        /// Ensure the active ARA profile's <c>ara-&lt;slug&gt;</c> PHD2 profile is selected on connect (§63.4),
        /// creating it on first connect. Runs after <c>GetProfiles</c> and before the §63.5 param push so the
        /// pushed scope/aggressiveness land in the right profile. The name path leaves the equipment
        /// disconnected for the connect path's single downstream reconnect; best-effort so a profile RPC can
        /// never fail the connect itself.
        /// </summary>
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Best-effort §63.4 profile-select boundary: a disconnect/select/create RPC may throw (socket drop, a daemon that rejects the name) — it's logged and skipped so profile mapping can never fail or block the guider connect.")]
        private async Task EnsureAraGuiderProfileAsync(CancellationToken ct) {
            var guider = profileService.ActiveProfile.GuiderSettings;
            var selection = ResolveAraProfileSelection(
                guider.PHD2ProfileId,
                SelectedProfile?.Id,
                profileService.ActiveProfile.Name,
                AvailableProfiles.ToList());

            switch (selection.Kind) {
                case AraProfileActionKind.None:
                    return;

                case AraProfileActionKind.SelectById:
                    // Explicit user override — the inherited path (disconnect → set_profile{id} → reconnect),
                    // which also persists PHD2ProfileId. Unchanged from the pre-§63.4 behavior.
                    await ChangeProfile(selection.Id);
                    return;
            }

            // Send-time guard (per the #375 review): never fire set_profile_by_name/create_profile with an
            // empty name. AraGuiderProfileName always yields a non-empty "ara-..." so this can't trip today,
            // but it keeps an empty name from ever reaching the daemon if the mapping changes. ARA never sets a
            // clone source, so create_profile's mutually-exclusive copy_from/copy_from_id stay unset by design.
            if (string.IsNullOrEmpty(selection.Name)) {
                Logger.Warning("PHD2 §63.4 - resolved an empty profile name; skipping select/create to avoid a malformed RPC.");
                return;
            }

            // §63.4 name path. Switching/creating a profile needs the equipment disconnected (mirrors
            // ChangeProfile); leave it disconnected so the connect path's PushGuiderEngineConfigAsync +
            // EnsurePHD2EquipmentConnected reconnect exactly once. Deliberately do NOT write
            // GuiderSettings.PHD2ProfileId here — that field is the user's manual override, and persisting it
            // would pin the auto-mapping (a later ARA-profile rename would stop re-mapping to the new slug).
            try {
                await DisconnectPHD2Equipment();
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                Logger.Warning($"PHD2 §63.4 - equipment disconnect before profile select failed: {ex.Message}");
            }

            var create = selection.Kind == AraProfileActionKind.Create;
            Phd2Method msg = create
                ? new Phd2CreateProfile { Parameters = new Phd2CreateProfileParameter { Name = selection.Name } }
                : new Phd2SetProfileByName { Parameters = new Phd2SetProfileByNameParameter { Name = selection.Name } };
            try {
                ct.ThrowIfCancellationRequested();
                var resp = await SendMessage(msg);
                if (resp?.error != null) {
                    Logger.Warning($"PHD2 §63.4 - {msg.Method} for '{selection.Name}' not applied: {resp.error}");
                } else {
                    Logger.Info($"PHD2 §63.4 - {(create ? "created" : "selected")} guider profile '{selection.Name}' for ARA profile '{profileService.ActiveProfile.Name}'.");
                }
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                Logger.Warning($"PHD2 §63.4 - {msg.Method} for '{selection.Name}' failed: {ex.Message}");
            }

            // Refresh AvailableProfiles + SelectedProfile to reflect the switch/create. Best-effort: the profile
            // was already selected on the daemon, so a failed refresh (GetProfiles throws on RPC error) must not
            // abort the connect — the next GetProfiles in the connect path / a later poll will reconcile.
            try {
                await GetProfiles();
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                Logger.Warning($"PHD2 §63.4 - profile refresh after select/create failed: {ex.Message}");
            }
        }
    }
}
