#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using OpenAstroAra.Server.Contracts;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §70 profile sharing — renders the equipment-stripped <c>profile-share-v1</c>
/// template (§70.2) for export. The stripping policy (§70.1, plus the
/// 2026-06-17 cross-OS "strip all paths" decision) drops everything that is
/// host-, rig-, location-, or secret-specific so the recipient re-wizards their
/// own gear (§70.4); only the donor's tuning judgement survives, alongside a
/// rig description of what the template targeted.
///
/// Import (§70.4) is the mirror: preview parses + validates a profile-share-v1
/// file and returns a short-lived token; commit creates a new (non-active)
/// profile from the template's settings, which the recipient then wizards their
/// own equipment into.
/// </summary>
public sealed class ProfileShareService : IProfileShareService {
    private readonly IProfileRepository _repo;
    private readonly TimeProvider _clock;

    /// Parsed manifests awaiting a commit, keyed by the preview's import token.
    /// In-memory + short-lived (§70.7): a preview the user never commits expires.
    private readonly ConcurrentDictionary<Guid, PendingImport> _pending = new();
    // Serializes prune + cap-check + insert so two concurrent previews can't both
    // pass the cap and overfill the store (the check and insert aren't otherwise atomic).
    private readonly object _previewGate = new();
    private static readonly TimeSpan ImportTtl = TimeSpan.FromMinutes(15);

    /// Cap on un-committed previews held at once. The store is pruned on every
    /// preview, but a caller that floods previews without committing could still
    /// grow it unboundedly within the TTL window — this bounds the blast radius
    /// (e.g. if the daemon is ever exposed to the LAN without auth).
    internal const int MaxPendingImports = 32;

    // Name is resolved once at preview (against the repo state then) and reused at
    // commit, so the profile is created under the exact name the user confirmed in
    // the preview dialog — not a freshly re-derived one that a concurrent import
    // could have shifted (the preview→commit TOCTOU). Names aren't a unique key
    // (the id is), so reusing the preview name is preferred over re-deduping.
    private sealed record PendingImport(ProfileShareManifest Manifest, string Name, DateTimeOffset ExpiresUtc);

    public ProfileShareService(IProfileRepository repo, TimeProvider? clock = null) {
        _repo = repo;
        _clock = clock ?? TimeProvider.System;
    }

    public Task<ProfileShareDto?> ExportAsync(Guid profileId, CancellationToken ct) {
        // GetProfile is synchronous today (in-memory under a lock), so there is
        // nothing to cancel mid-flight; honor ct up front so the contract holds and
        // it's already threaded when the repo grows async disk I/O.
        ct.ThrowIfCancellationRequested();
        var stored = _repo.GetProfile(profileId);
        if (stored is null) {
            return Task.FromResult<ProfileShareDto?>(null);
        }

        // Order matters: capture the rig description from the ORIGINAL settings
        // before stripping zeros out optics + guide geometry. Reversing these two
        // lines would publish an all-zero rig_description.
        var rig = BuildRigDescription(stored.Settings);
        var stripped = StripForShare(stored.Settings);
        var manifest = new ProfileShareManifest(
            SchemaVersion: ProfileShareManifest.CurrentSchemaVersion,
            SharedAt: DateTimeOffset.UtcNow,
            SourceAraVersion: SourceAraVersion,
            // §70.3 donor attribution is opt-in at export time; the toggle + comment
            // arrive with the export-options request body in a follow-up. For now the
            // profile name is the only attribution and the donor block is omitted.
            Donor: null,
            RigDescription: rig,
            Settings: stripped);

        // Serialize once to UTF-8 (source-gen, AOT-safe), then parse back to a
        // JsonElement: ProfileShareDto.Manifest is typed as JsonElement (returned
        // inline), and this is the AOT-safe way to get one from a typed object
        // without reflection. The same byte[] also gives the true PayloadBytes, so
        // the round-trip isn't redundant — don't "optimize" it into a single step.
        var utf8 = JsonSerializer.SerializeToUtf8Bytes(
            manifest, AraJsonSerializerContext.Default.ProfileShareManifest);
        using var doc = JsonDocument.Parse(utf8);
        var manifestElement = doc.RootElement.Clone();

        var dto = new ProfileShareDto(
            ProfileId: profileId,
            ProfileName: stored.Meta.Name,
            Manifest: manifestElement,
            PayloadBytes: utf8.Length,
            // The manifest is returned inline (the client writes it straight to the
            // chosen file); there is no separate payload route to download, so this
            // stays null rather than pointing at an unmapped URL.
            DownloadUrl: null);
        return Task.FromResult<ProfileShareDto?>(dto);
    }

    /// <summary>
    /// §70.1 stripping applied to ARA's actual section DTOs. Keeps tuning
    /// judgement; drops paths (§decision), secrets, donor location + network,
    /// and rig geometry (lifted into the rig description instead).
    /// </summary>
    /// <remarks>
    /// SECURITY — keep/strip ledger for ALL 13 sections. This is an allowlist by
    /// omission (a section is shared verbatim unless a <c>with</c>-clause below
    /// strips fields), so adding a field to any section DTO ships it in the share
    /// file by default. When you add a field, decide here and update
    /// <c>ProfileShareServiceTest</c> (a sentinel-scan test asserts no path/identity
    /// value leaks into the payload):
    /// <list type="bullet">
    ///   <item>ImagingDefaults — KEEP (exposure/gain/cooler tuning, no PII)</item>
    ///   <item>Storage — STRIP save_directory + filename_template (paths); keep format/compression/disk-thresholds</item>
    ///   <item>Notifications — STRIP pushover_token + telegram_bot_token (SECRETS); keep channel/trigger toggles</item>
    ///   <item>Site — STRIP name/lat/long/elevation/timezone (location); keep bortle/seeing/twilight/horizon</item>
    ///   <item>Filenames — KEEP (date-separator enum + compress toggle, not a path)</item>
    ///   <item>SafetyPolicies — KEEP (tuning judgement)</item>
    ///   <item>Autofocus — KEEP (method/sweep/trigger judgement; af_filter is a harmless name)</item>
    ///   <item>PlateSolve — STRIP path_or_endpoint + index_download_path (paths); keep engine/knobs</item>
    ///   <item>DiagnosticsMode — KEEP (single enum)</item>
    ///   <item>Phd2 — STRIP host/port/profile (network) + guide geometry (→ rig desc); keep dither/settle/aggressiveness</item>
    ///   <item>EquipmentConnection — KEEP (auto-connect bools per device type, no PII)</item>
    ///   <item>StretchDefaults — KEEP (display judgement)</item>
    ///   <item>Optics — STRIP all (rig geometry → rig description; includes aperture_mm)</item>
    ///   <item>CameraElectronics — STRIP all (donor's camera hardware, incl. sensor_name; recipient's camera differs and auto-captures on connect)</item>
    ///   <item>FilterSet — STRIP all (donor's physical filters; recipient declares their own)</item>
    /// </list>
    /// </remarks>
    internal static ProfileSnapshotDto StripForShare(ProfileSnapshotDto s) => s with {
        // Strip all host/OS-specific paths — un-remappable across OSes, so the
        // recipient sets save dir + filename template themselves in the wizard.
        Storage = s.Storage with { SaveDirectory = "", FilenameTemplate = "" },
        // Strip ASTAP executable + index paths (host-specific).
        PlateSolve = s.PlateSolve with { PathOrEndpoint = "", IndexDownloadPath = "" },
        // Strip secrets — push/telegram credentials must never travel in a share file
        // (BOTH halves of each channel: token + user key, bot token + chat id).
        Notifications = s.Notifications with {
            PushoverToken = "", TelegramBotToken = "",
            PushoverUserKey = "", TelegramChatId = "",
        },
        // §36 custom terrain horizon is location-revealing terrain data (a skyline
        // fingerprints the site) — strip it entirely, same stance as the coordinates.
        CustomHorizon = new CustomHorizonDto(Points: []),
        // Strip donor location (lat/long/elevation/timezone/name); keep sky-quality
        // judgement (bortle/seeing/twilight/horizon) as informational context.
        Site = s.Site with {
            SiteName = "",
            LatitudeDeg = 0,
            LongitudeDeg = 0,
            ElevationM = 0,
            TimeZone = "",
        },
        // Strip donor network + guide-scope geometry (the geometry is lifted into
        // the rig description); keep the dither/settle/aggressiveness tuning.
        Phd2 = s.Phd2 with {
            Host = "",
            Port = DefaultPhd2Port,
            Phd2Profile = "",
            GuideFocalLength = 0,
            GuidePixelSize = 0,
        },
        // Strip optics geometry (recipient's optics differ); preserved in the rig
        // description so the recipient can judge applicability.
        Optics = s.Optics with {
            FocalLengthMm = 0,
            ReducerFactor = 1.0,
            SensorWidthPx = 0,
            SensorHeightPx = 0,
            PixelSizeUm = 0,
            ApertureMm = 0,
        },
        // Strip the donor's camera hardware — the recipient's camera differs and
        // its ASCOM-sourced fields auto-capture on their first connect anyway.
        CameraElectronics = new CameraElectronicsDto(),
        // Strip the donor's physical filter list — the recipient declares their own.
        FilterSet = new FilterSetDto(Filters: []),
        // Strip the §59.2 Smart Focus calibration — daemon-derived data fitted to the donor's
        // focuser + optical train; on the recipient's rig it would feed the one-frame runner a
        // wrong map (the fallback ladder would catch it, but a share must not plant bad state).
        FocusCalibration = null,
        // Strip the §30.7.4 calibration-state stamps — the donor's dark library / defect map live
        // on the donor's guider host; "valid" on the recipient's rig would be a lie.
        CalibrationState = CalibrationStateDto.Empty,
    };

    private static ProfileShareRigDescriptionDto BuildRigDescription(ProfileSnapshotDto s) =>
        new(
            FocalLengthMm: s.Optics.FocalLengthMm,
            ReducerFactor: s.Optics.ReducerFactor,
            EffectiveFocalLengthMm: s.Optics.FocalLengthMm * s.Optics.ReducerFactor,
            SensorWidthPx: s.Optics.SensorWidthPx,
            SensorHeightPx: s.Optics.SensorHeightPx,
            PixelSizeUm: s.Optics.PixelSizeUm,
            GuideScopeFocalLengthMm: s.Phd2.GuideFocalLength);

    /// <summary>PHD2's default port (§63); used as the neutral value once the
    /// donor's host/port are stripped.</summary>
    private const int DefaultPhd2Port = 4400;

    /// <summary>The assembly version (the part before any <c>+gitSha</c> suffix in
    /// the informational version), e.g. <c>0.0.1-ara.6</c>; <c>unknown</c> if absent.
    /// Computed once at class load — the value never changes at runtime, so this
    /// avoids reflecting on every export.</summary>
    private static readonly string SourceAraVersion = ComputeSourceAraVersion();

    private static string ComputeSourceAraVersion() {
        var info = typeof(ProfileShareService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(info)) return "unknown";
        var plus = info.IndexOf('+');
        return plus >= 0 ? info[..plus] : info;
    }

    // ─── §70.4 import (preview + commit) ───

    /// The categories the export strips (§70.1) — surfaced to the importer so they
    /// know what they must supply themselves after importing the template. This is a
    /// hand-maintained mirror of what ExportAsync actually strips; keep the two in
    /// step if the strip set changes (drift risk tracked in design/PORT_TODO.md).
    private static readonly IReadOnlyList<string> DroppedFields = new[] {
        "Equipment (camera / mount / focuser / …) — re-select in the wizard",
        "Save directory + filename template",
        "ASTAP executable + index paths",
        "Site location (latitude / longitude / elevation / timezone)",
        "PHD2 host / port / profile",
        "Notification tokens",
        "Autofocus calibration — regenerates on your first autofocus sweep",
    };

    // Returns Task.FromException (never throws synchronously) so every exit — including
    // validation/throttle failures — surfaces through the returned Task, honouring the
    // async contract for any non-awaiting caller (.Result, .ContinueWith, …).
    public Task<ProfileShareImportPreviewDto> ImportPreviewAsync(JsonElement manifest, CancellationToken ct) {
        if (ct.IsCancellationRequested) {
            return Task.FromCanceled<ProfileShareImportPreviewDto>(ct);
        }
        ProfileShareManifest? parsed;
        try {
            parsed = manifest.Deserialize(AraJsonSerializerContext.Default.ProfileShareManifest);
        } catch (JsonException) {
            parsed = null;
        }
        // Reject anything that isn't a profile-share-v1 file with settings + a rig
        // description (→ 422), so a wrong/garbled upload fails clearly instead of
        // creating junk. Both are non-nullable on the record, but source-gen JSON
        // won't enforce that — a file omitting either deserializes them to null.
        if (parsed is null ||
            parsed.SchemaVersion != ProfileShareManifest.CurrentSchemaVersion ||
            parsed.Settings is null ||
            parsed.RigDescription is null) {
            return Task.FromException<ProfileShareImportPreviewDto>(new InvalidProfileShareException(
                $"Not a recognized profile share file (expected schema_version '{ProfileShareManifest.CurrentSchemaVersion}')."));
        }

        var token = Guid.NewGuid();
        var expires = _clock.GetUtcNow().Add(ImportTtl);
        // Resolve the name BEFORE the gate: MakeUniqueName reads the repo (_repo.List()),
        // and the name depends only on the repo + manifest, not on _pending — so keeping
        // it out of the lock stops a repo round-trip from serializing concurrent previews.
        // Stored on PendingImport so commit reuses it verbatim (see PendingImport).
        var importedName = ImportedName(parsed);
        // Prune + cap-check + insert under one gate so concurrent previews can't both
        // pass the cap and overfill the store. Pruning first means expired entries
        // don't count against the cap.
        lock (_previewGate) {
            PruneExpired();
            if (_pending.Count >= MaxPendingImports) {
                return Task.FromException<ProfileShareImportPreviewDto>(new ProfileShareImportThrottledException(
                    "Too many pending profile-share imports — commit or wait for one to expire, then retry."));
            }
            _pending[token] = new PendingImport(parsed, importedName, expires);
        }

        var rig = parsed.RigDescription;
        var warnings = new List<string> {
            "This is a template, not a complete profile — you'll set up your own equipment after importing.",
            $"Designed for ~{rig.EffectiveFocalLengthMm:0} mm effective focal length"
                + (rig.PixelSizeUm > 0 ? $" with a {rig.PixelSizeUm:0.##} µm sensor." : "."),
        };

        return Task.FromResult(new ProfileShareImportPreviewDto(
            ImportToken: token,
            ProfileName: importedName,
            Warnings: warnings,
            DroppedFields: DroppedFields,
            ExpiresUtc: expires));
    }

    // Likewise returns Task.FromException rather than throwing synchronously.
    public Task<Guid> ImportCommitAsync(Guid importToken, CancellationToken ct) {
        if (ct.IsCancellationRequested) {
            return Task.FromCanceled<Guid>(ct);
        }
        // Single-use: TryRemove atomically claims the token (only one caller wins).
        // Expiry is enforced here on the claimed entry — not via PruneExpired — so an
        // expired token is rejected deterministically rather than racing the sweep.
        if (!_pending.TryRemove(importToken, out var pending) ||
            pending.ExpiresUtc < _clock.GetUtcNow()) {
            return Task.FromException<Guid>(new ProfileShareImportTokenException(
                "Import token is unknown or expired — preview the share file again."));
        }
        // Create as a NON-active profile from the template's (already-stripped)
        // settings, under the name resolved at preview time (PendingImport.Name);
        // the recipient selects it and wizards their own equipment in.
        var meta = _repo.Create(pending.Name, pending.Manifest.Settings, makeActive: false);
        return Task.FromResult(meta.Id);
    }

    // The display name a freshly-imported template gets, de-duplicated against the
    // names already in the repo so a second import of the same rig doesn't collide
    // with the first ("Imported — 2032 mm rig" → "Imported — 2032 mm rig (2)").
    // Called at both preview (so the user sees the real name up front) and commit
    // (authoritative against the repo state at create time).
    private string ImportedName(ProfileShareManifest m) => MakeUniqueName(ImportedBaseName(m));

    // The base label before de-duplication. Prefers the donor's opt-in display name
    // (§70.3 — reserved for a future opt-in export mode / hand-authored manifest, not
    // yet reachable via the shipped export path); otherwise derives a non-identifying
    // label from the rig's effective focal length so repeated imports are
    // distinguishable at a glance; falls back to a neutral label when neither is known.
    private static string ImportedBaseName(ProfileShareManifest m) {
        if (m.Donor?.DisplayName is { Length: > 0 } name) {
            return name;
        }
        // Upper bound rejects a crafted/garbage manifest (∞, NaN — which fails both
        // comparisons — or an absurd value); 1,000,000 mm is ~1 km, far beyond any
        // real instrument. Formatting with "0" (not an int cast, which would overflow
        // to int.MinValue for huge values) matches the rounding used for the preview
        // warning text below, so the name and the warning never diverge.
        var efl = m.RigDescription?.EffectiveFocalLengthMm ?? 0;
        if (efl > 0 && efl < 1_000_000) {
            return $"Imported — {efl:0} mm rig";
        }
        return "Imported profile";
    }

    // Appends " (2)", " (3)", … until the name is free. Match is case-insensitive to
    // mirror how a user perceives duplicates; the loop is bounded by the number of
    // existing profiles, so it always terminates.
    private string MakeUniqueName(string baseName) {
        var existing = new HashSet<string>(
            _repo.List().Profiles.Select(p => p.Name),
            StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(baseName)) {
            return baseName;
        }
        for (var n = 2; ; n++) {
            var candidate = $"{baseName} ({n})";
            if (!existing.Contains(candidate)) {
                return candidate;
            }
        }
    }

    private void PruneExpired() {
        var now = _clock.GetUtcNow();
        foreach (var kv in _pending) {
            if (kv.Value.ExpiresUtc < now) _pending.TryRemove(kv.Key, out _);
        }
    }
}

/// <summary>The uploaded bytes aren't a recognized <c>profile-share-v1</c> file
/// (bad JSON / wrong schema version / missing settings) — maps to 422.</summary>
public sealed class InvalidProfileShareException : Exception {
    public InvalidProfileShareException() { }
    public InvalidProfileShareException(string message) : base(message) { }
    public InvalidProfileShareException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>The import token is unknown or expired (already committed, or the
/// 15-minute preview window lapsed) — maps to 404.</summary>
public sealed class ProfileShareImportTokenException : Exception {
    public ProfileShareImportTokenException() { }
    public ProfileShareImportTokenException(string message) : base(message) { }
    public ProfileShareImportTokenException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Too many un-committed previews are held at once (the pending-import
/// cap) — maps to 429 so the caller backs off rather than growing the store.</summary>
public sealed class ProfileShareImportThrottledException : Exception {
    public ProfileShareImportThrottledException() { }
    public ProfileShareImportThrottledException(string message) : base(message) { }
    public ProfileShareImportThrottledException(string message, Exception innerException) : base(message, innerException) { }
}
