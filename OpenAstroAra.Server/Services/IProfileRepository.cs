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
using OpenAstroAra.Server.Contracts;

namespace OpenAstroAra.Server.Services;

/// <summary>Outcome of <see cref="IProfileRepository.Delete"/>, decided atomically under the
/// repository lock so the caller can map a status code without a racy pre-read.</summary>
public enum ProfileDeleteResult {
    Deleted,
    NotFound,
    RefusedActive,
    RefusedLastRemaining,
}

/// <summary>
/// §37 multi-profile management — the known-profiles set (§30) backed by
/// <c>{profileDir}/profiles/{id}.json</c>. The active profile's live settings flow
/// through <see cref="IProfileStore"/> as before; this repository owns the set of
/// named profiles and which one is active, and keeps the active profile's file in
/// sync with the live store.
/// </summary>
public interface IProfileRepository : IDisposable {
    /// <summary>The known profiles + which is active.</summary>
    ProfileListDto List();

    /// <summary>Currently-active profile id, or null if none exists yet.</summary>
    Guid? ActiveId { get; }

    /// <summary>Full saved profile, or null if no such id.</summary>
    StoredProfileDto? GetProfile(Guid id);

    /// <summary>
    /// Create a new saved profile. When <paramref name="settings"/> is null the current
    /// active profile's live settings are cloned. When <paramref name="makeActive"/> is
    /// true it also becomes active (its settings are loaded into the live store).
    /// <para>Note: if there is no active profile yet (the very first create, or a recovered
    /// empty set), the new profile is made active <b>regardless</b> of
    /// <paramref name="makeActive"/> — there must always be an active profile.</para>
    /// </summary>
    ProfileMetaDto Create(string name, ProfileSnapshotDto? settings, bool makeActive);

    /// <summary>Rename a profile. False if the id is unknown.</summary>
    bool Rename(Guid id, string name);

    /// <summary>
    /// Delete a profile. Refused for the active profile or the last remaining profile —
    /// there must always be an active profile to fall back to. The decision is made
    /// atomically; see <see cref="ProfileDeleteResult"/>.
    /// </summary>
    ProfileDeleteResult Delete(Guid id);

    /// <summary>
    /// Make <paramref name="id"/> active: load its settings into the live store and
    /// update the active pointer. False if the id is unknown.
    /// </summary>
    bool SelectProfile(Guid id);
}
