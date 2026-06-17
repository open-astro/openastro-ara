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
/// Import preview/commit (§70.4) land in a follow-up sub-PR; they retain the
/// placeholder behavior here so the endpoints stay wired.
/// </summary>
public sealed class ProfileShareService : IProfileShareService {
    private readonly IProfileRepository _repo;

    public ProfileShareService(IProfileRepository repo) => _repo = repo;

    public Task<ProfileShareDto?> ExportAsync(Guid profileId, CancellationToken ct) {
        var stored = _repo.GetProfile(profileId);
        if (stored is null) {
            return Task.FromResult<ProfileShareDto?>(null);
        }

        var rig = BuildRigDescription(stored.Settings);
        var stripped = StripForShare(stored.Settings);
        var manifest = new ProfileShareManifest(
            SchemaVersion: ProfileShareManifest.CurrentSchemaVersion,
            SharedAt: DateTimeOffset.UtcNow,
            SourceAraVersion: SourceAraVersion(),
            // §70.3 donor attribution is opt-in at export time; the toggle + comment
            // arrive with the export-options request body in a follow-up. For now the
            // profile name is the only attribution and the donor block is omitted.
            Donor: null,
            RigDescription: rig,
            Settings: stripped);

        var utf8 = JsonSerializer.SerializeToUtf8Bytes(
            manifest, AraJsonSerializerContext.Default.ProfileShareManifest);
        using var doc = JsonDocument.Parse(utf8);
        var manifestElement = doc.RootElement.Clone();

        var dto = new ProfileShareDto(
            ProfileId: profileId,
            ProfileName: stored.Meta.Name,
            Manifest: manifestElement,
            PayloadBytes: utf8.Length,
            DownloadUrl: new Uri($"/api/v1/profiles/share/{profileId}/payload", UriKind.Relative));
        return Task.FromResult<ProfileShareDto?>(dto);
    }

    /// <summary>
    /// §70.1 stripping applied to ARA's actual section DTOs. Keeps tuning
    /// judgement; drops paths (§decision), secrets, donor location + network,
    /// and rig geometry (lifted into the rig description instead).
    /// </summary>
    internal static ProfileSnapshotDto StripForShare(ProfileSnapshotDto s) => s with {
        // Strip all host/OS-specific paths — un-remappable across OSes, so the
        // recipient sets save dir + filename template themselves in the wizard.
        Storage = s.Storage with { SaveDirectory = "", FilenameTemplate = "" },
        // Strip ASTAP executable + index paths (host-specific).
        PlateSolve = s.PlateSolve with { PathOrEndpoint = "", IndexDownloadPath = "" },
        // Strip secrets — push/telegram tokens must never travel in a share file.
        Notifications = s.Notifications with { PushoverToken = "", TelegramBotToken = "" },
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
        },
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
    /// the informational version), e.g. <c>0.0.1-ara.6</c>; <c>unknown</c> if absent.</summary>
    private static string SourceAraVersion() {
        var info = typeof(ProfileShareService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(info)) return "unknown";
        var plus = info.IndexOf('+');
        return plus >= 0 ? info[..plus] : info;
    }

    // ─── §70.4 import (preview + commit) — placeholder until the import sub-PR ───

    private static readonly JsonDocument _emptyManifest = JsonDocument.Parse("{}");

    public Task<ProfileShareImportPreviewDto> ImportPreviewAsync(JsonElement manifest, CancellationToken ct) {
        _ = manifest;
        return Task.FromResult(new ProfileShareImportPreviewDto(
            ImportToken: Guid.NewGuid(),
            ProfileName: "Imported profile (placeholder)",
            Warnings: Array.Empty<string>(),
            DroppedFields: Array.Empty<string>(),
            ExpiresUtc: DateTimeOffset.UtcNow.AddMinutes(15)));
    }

    public Task<Guid> ImportCommitAsync(Guid importToken, CancellationToken ct) {
        _ = importToken;
        _ = _emptyManifest;
        return Task.FromResult(Guid.NewGuid());
    }
}
