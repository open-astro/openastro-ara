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
using System.Linq;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// Bridges the section-oriented <see cref="IProfileStore"/> and the whole-profile
/// <see cref="ProfileSnapshotDto"/>: <see cref="Capture"/> reads every section out,
/// <see cref="Apply"/> writes a full snapshot back (one Put per section). Used by the
/// multi-profile repository to clone the live profile and to load a saved profile into
/// the active working copy. Both walk the section table (<see cref="ProfileSections"/>), so a
/// section cannot be left out of one half (#1264 was the unguided block Apply missed).
/// </summary>
public static class ProfileStoreSnapshot {
    public static ProfileSnapshotDto Capture(IProfileStore s) =>
        ProfileSections.All.Aggregate(ProfileSections.Defaults, (snap, section) => section.Capture(s, snap));

    /// <summary>Push every section of <paramref name="snap"/> into the live store.
    /// Each Put raises <see cref="IProfileStore.Changed"/>, so callers that don't want
    /// one change notification per section (e.g. profile-select) should suppress their own
    /// handler for the duration. A section absent from the snapshot takes its table default, so
    /// selecting a profile never keeps the previous rig's value; a null calibration is meaningful
    /// ("never measured") and clears the live one.</summary>
    public static void Apply(IProfileStore s, ProfileSnapshotDto snap) {
        foreach (var section in ProfileSections.All) {
            section.Apply(s, snap);
        }
    }
}