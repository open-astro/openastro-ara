#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Server.Contracts;
using System;
using System.Linq;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// The single home for "a profile snapshot never has null sections". A section
/// key missing from an older on-disk JSON deserializes the (non-nullable)
/// record parameter to null rather than throwing — and that null then either
/// ships on the wire (a <c>"camera_electronics": null</c> GET body) or NREs in
/// a consumer (the null-optics → <c>Optics().ApertureMm</c> path that once
/// 500'd /planning/optimal-sub).
///
/// Shared by BOTH places snapshots are born from disk: the active-profile
/// store (<see cref="FileProfileStore"/>, load + every write) and the
/// multi-profile repository (<see cref="FileProfileRepository.ReadFile"/>,
/// which serves GET /api/v1/profiles/{id} and feeds profile-share export) —
/// normalizing only the former was the #689 gap this closes.
/// </summary>
public static class ProfileSnapshotNormalizer {

    /// <summary>
    /// What a fresh profile holds, read from the section table (<see cref="ProfileSections.Defaults"/>).
    /// Used on first boot (no profile.json), on parse failure (corrupt file), and as the
    /// per-section back-fill in <see cref="Normalize(ProfileSnapshotDto)"/>.
    /// </summary>
    public static ProfileSnapshotDto Defaults => ProfileSections.Defaults;

    /// <summary>Back-fills every null section (and the known null inner values, such as
    /// a <c>"filter_set": {}</c> that yields a DTO with a null list) from the section table, and applies
    /// each section's repair (retired format tokens, stock solver paths). Null calibrations pass through.</summary>
    public static ProfileSnapshotDto Normalize(ProfileSnapshotDto snap) => Normalize(snap, System.IO.File.Exists);

    /// <param name="fileExists">The file check <see cref="SolverPathMigration"/> uses (#1121);
    /// injected so tests never depend on what this machine has in /usr/bin.</param>
    public static ProfileSnapshotDto Normalize(ProfileSnapshotDto snap, Func<string, bool> fileExists) =>
        ProfileSections.All.Aggregate(snap, (acc, section) => section.Normalize(acc, fileExists));
}