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
        /// <summary>The ARA profile's twin exists under its display name — select it by name.</summary>
        SelectByName,
        /// <summary>No twin exists yet — create + select it under the ARA profile's display name.</summary>
        Create,
        /// <summary>A legacy <c>ara-&lt;slug&gt;-&lt;id8&gt;</c> twin exists — rename it in place to the
        /// display name (dark library and tuning ride along), then select it.</summary>
        RenameLegacy,
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
        /// §63.4 — resolves the ACTIVE ARA profile's identity (id + display name) from the server's
        /// multi-profile repository. The Equipment layer's own <c>IProfileService.ActiveProfile</c> is the
        /// legacy single-profile store whose name is a constant "Default" — mapping the guider twin from it
        /// produced a daemon profile named "Default" regardless of what the user called their rig. Null (or
        /// a null result) falls back to the legacy store, which keeps benches and tests working unwired.
        /// Set by the owning service right after construction; not part of the constructor because the
        /// repository lives in the Server layer.
        /// </summary>
        public Func<(System.Guid Id, string? Name)?>? AraProfileResolver { get; set; }

        private (System.Guid Id, string? Name) ResolveAraProfileIdentity() {
            var resolved = AraProfileResolver?.Invoke();
            return resolved ?? (profileService.ActiveProfile.Id, profileService.ActiveProfile.Name);
        }

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
        /// Pure + socket-free so it's unit-testable without a live guider. This bare-slug form can collide
        /// (two names that slug the same → one PHD2 profile); the connect path uses the id-suffixed
        /// <see cref="AraGuiderProfileName(string?, System.Guid)"/> overload to keep each ARA profile distinct.
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
        /// The collision-free PHD2 profile name for an ARA profile (guider-e-3c): the bare
        /// <see cref="AraGuiderProfileName(string?)"/> slug plus a short suffix from the ARA profile's stable Id
        /// (<c>ara-&lt;slug&gt;-&lt;id8&gt;</c>, e.g. <c>ara-c14-cem120-a3f8e1c2</c>). The suffix makes the name
        /// deterministic <em>per ARA profile</em> yet unique <em>across</em> profiles, so two profiles whose
        /// names slug identically (<c>"C-14"</c> / <c>"C 14"</c> → <c>ara-c-14</c>) still map to distinct PHD2
        /// profiles. This needs no stored "resolved name" on the ARA profile — which matters because the
        /// Equipment-layer guider has read-only <see cref="IProfileService"/> access and can't persist one back.
        /// </summary>
        public static string AraGuiderProfileName(string? araProfileName, System.Guid araProfileId) =>
            AraGuiderProfileName(araProfileName) + "-" + araProfileId.ToString("N")[..8];

        /// <summary>
        /// The guider-side profile name for an ARA profile: the ARA profile's DISPLAY NAME, verbatim
        /// (trimmed). The slugged <c>ara-&lt;slug&gt;-&lt;id8&gt;</c> form read as machine noise in the
        /// guider's own UI, so the twin now carries the exact name the user gave the rig ("Backyard RC8",
        /// not "ara-backyard-rc8-a3f8e1c2"). A name that trims to empty falls back to <c>"Ara Default"</c>.
        /// PHD2 profile names are arbitrary strings, so no character sanitizing is needed.
        /// </summary>
        /// <remarks>
        /// ARA profile names are NOT a unique key, so two identically-named ARA profiles now share one
        /// guider twin (shared tuning + dark library) — the same-name case is exactly when a user would
        /// expect that, and the legacy id-suffix scheme bought its uniqueness at the cost of unreadable
        /// names. Legacy twins are migrated by rename on connect (see
        /// <see cref="ResolveAraProfileSelection"/>'s <see cref="AraProfileActionKind.RenameLegacy"/>).
        /// </remarks>
        public static string AraGuiderDisplayProfileName(string? araProfileName) {
            var trimmed = (araProfileName ?? string.Empty).Trim();
            return trimmed.Length == 0 ? "Ara Default" : trimmed;
        }

        /// <summary>
        /// Does <paramref name="daemonProfileName"/> look like this ARA profile's LEGACY
        /// <c>ara-&lt;slug&gt;-&lt;id8&gt;</c> twin? Matched by the <c>ara-</c> prefix plus the id suffix
        /// (not the slug), so a twin created under an older ARA profile NAME still matches after renames —
        /// the id8 is derived from the ARA profile's stable Id.
        /// </summary>
        public static bool IsLegacyAraGuiderProfileName(string? daemonProfileName, System.Guid araProfileId) =>
            daemonProfileName is not null &&
            daemonProfileName.StartsWith("ara-", StringComparison.Ordinal) &&
            daemonProfileName.EndsWith("-" + araProfileId.ToString("N")[..8], StringComparison.Ordinal);

        /// <summary>
        /// Decide what profile action a guider connect should take (§63.4), pure + socket-free so the connect
        /// path's choice is unit-testable. Precedence: an explicit <paramref name="overrideProfileId"/> (the
        /// inherited <c>GuiderSettings.PHD2ProfileId</c> — the user's manual override) wins and selects by id;
        /// otherwise the ARA profile maps to its id-suffixed <c>ara-&lt;slug&gt;-&lt;id8&gt;</c> PHD2 profile
        /// (guider-e-3c, collision-free), selected by name if it already exists or created if not. When the
        /// target is already the selected profile, the result is <see cref="AraProfileActionKind.None"/> so
        /// connect doesn't needlessly drop the equipment.
        /// </summary>
        public static AraProfileSelection ResolveAraProfileSelection(
            int? overrideProfileId,
            int? selectedProfileId,
            string? activeAraProfileName,
            System.Guid activeAraProfileId,
            IReadOnlyList<Phd2Profile> availableProfiles) {

            if (overrideProfileId.HasValue) {
                return selectedProfileId == overrideProfileId.Value
                    ? new AraProfileSelection(AraProfileActionKind.None, 0, string.Empty)
                    : new AraProfileSelection(AraProfileActionKind.SelectById, overrideProfileId.Value, string.Empty);
            }

            // The twin carries the ARA profile's display name verbatim. The exists-check is
            // case-insensitive because the daemon's own name checks are (rename_profile /
            // create_profile compare CmpNoCase) — an Ordinal match here could decide to create a
            // name the daemon would then reject as "already exists".
            var araName = AraGuiderDisplayProfileName(activeAraProfileName);
            var existing = availableProfiles.FirstOrDefault(
                p => string.Equals(p.Name, araName, StringComparison.OrdinalIgnoreCase));
            if (existing != null) {
                return selectedProfileId == existing.Id
                    ? new AraProfileSelection(AraProfileActionKind.None, existing.Id, araName)
                    : new AraProfileSelection(AraProfileActionKind.SelectByName, existing.Id, araName);
            }
            // No display-name twin — migrate a legacy ara-<slug>-<id8> twin by rename (its dark
            // library and tuning ride along) rather than creating a fresh, empty profile beside it.
            var legacy = availableProfiles.FirstOrDefault(
                p => IsLegacyAraGuiderProfileName(p.Name, activeAraProfileId));
            if (legacy != null) {
                return new AraProfileSelection(AraProfileActionKind.RenameLegacy, legacy.Id, araName);
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
            var (araProfileId, araProfileName) = ResolveAraProfileIdentity();
            var selection = ResolveAraProfileSelection(
                guider.PHD2ProfileId,
                SelectedProfile?.Id,
                araProfileName,
                araProfileId,
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

            // Legacy-twin migration: rename ara-<slug>-<id8> to the display name in place (needs the
            // equipment disconnected — done above), then fall through to select it by its new name.
            // A failed rename skips the select: creating/selecting would either duplicate the twin or
            // pick a name that doesn't exist; the next connect retries the migration.
            if (selection.Kind == AraProfileActionKind.RenameLegacy) {
                try {
                    ct.ThrowIfCancellationRequested();
                    var renameResp = await SendMessage(new Phd2RenameProfile {
                        Parameters = new Phd2RenameProfileParameter { Id = selection.Id, NewName = selection.Name }
                    });
                    if (renameResp?.error != null) {
                        Logger.Warning($"PHD2 §63.4 - rename_profile {selection.Id} → '{selection.Name}' not applied: {renameResp.error}");
                        return;
                    }
                    Logger.Info($"PHD2 §63.4 - migrated legacy guider profile {selection.Id} to '{selection.Name}' (dark library preserved).");
                } catch (OperationCanceledException) {
                    throw;
                } catch (Exception ex) {
                    Logger.Warning($"PHD2 §63.4 - rename_profile {selection.Id} → '{selection.Name}' failed: {ex.Message}");
                    return;
                }
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
                    Logger.Info($"PHD2 §63.4 - {(create ? "created" : "selected")} guider profile '{selection.Name}' for ARA profile '{araProfileName ?? "(unnamed)"}'.");
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

        /// <summary>
        /// §63.4 delete hook — remove the named PHD2 profile (and its dark library / defect map,
        /// <c>delete_dark_files=true</c> per the lifecycle table). Returns true when the daemon accepted the
        /// delete, false on an RPC error response (e.g. the profile doesn't exist — already-gone is the
        /// caller's success case to decide). Throws only on transport faults; the caller owns best-effort.
        /// The daemon rejects deleting its SELECTED profile — callers must check
        /// <see cref="SelectedProfile"/> first (the lifecycle hook does; the selected twin tracks the last
        /// connect, not ARA's active-profile flag, so an ARA-side guard alone doesn't rule it out).
        /// </summary>
        public async Task<bool> DeleteGuiderProfileAsync(string name, CancellationToken ct) {
            ArgumentException.ThrowIfNullOrEmpty(name);
            ct.ThrowIfCancellationRequested();
            var msg = new Phd2DeleteProfile {
                Parameters = new Phd2DeleteProfileParameter { Name = name, DeleteDarkFiles = true },
            };
            var resp = await SendMessage(msg);
            if (resp?.error != null) {
                Logger.Warning($"PHD2 §63.4 - delete_profile for '{name}' not applied: {resp.error}");
                return false;
            }
            Logger.Info($"PHD2 §63.4 - deleted guider profile '{name}' (dark files included).");
            return true;
        }
    }
}
