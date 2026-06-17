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

namespace OpenAstroAra.Server.Contracts;

/// <summary>
/// §70.2 <c>profile-share-v1</c> wire format — the self-contained JSON a user
/// hands to a friend (email / USB / Discord) as an equipment-stripped template.
/// This is the shape serialized into <see cref="ProfileShareDto.Manifest"/>.
///
/// Distinct from the §43 backup schema (which keeps everything for same-user
/// disaster recovery): a share strips every host-, rig-, location-, and
/// secret-specific field so the recipient re-wizards their own gear (§70.4).
/// What survives is the donor's <em>tuning judgement</em> (dither/AF/safety/…)
/// plus a <see cref="ProfileShareRigDescriptionDto"/> describing what the
/// template was designed for.
/// </summary>
public sealed record ProfileShareManifest(
    string SchemaVersion,
    DateTimeOffset SharedAt,
    string SourceAraVersion,
    ProfileShareDonorDto? Donor,
    ProfileShareRigDescriptionDto RigDescription,
    ProfileSnapshotDto Settings) {
    /// <summary>The only schema version this server emits + accepts for profile shares.</summary>
    public const string CurrentSchemaVersion = "profile-share-v1";
}

/// <summary>
/// §70.3 optional donor attribution. Omitted entirely when the donor opts out
/// of including their name at export time.
/// </summary>
public sealed record ProfileShareDonorDto(
    string DisplayName,
    string? Comment);

/// <summary>
/// §70.2 <c>rigDescription</c> — capabilities + geometry the template was
/// designed for, never serials or driver IDs. Tells the recipient "this was
/// tuned for a 2032 mm SCT with a 3.76 µm sensor" so they can judge
/// applicability (§70.4 compatibility check). Geometry is lifted out of the
/// stripped <see cref="ProfileSnapshotDto.Optics"/> + guide-scope fields.
/// </summary>
public sealed record ProfileShareRigDescriptionDto(
    double FocalLengthMm,
    double ReducerFactor,
    double EffectiveFocalLengthMm,
    int SensorWidthPx,
    int SensorHeightPx,
    double PixelSizeUm,
    int GuideScopeFocalLengthMm);
