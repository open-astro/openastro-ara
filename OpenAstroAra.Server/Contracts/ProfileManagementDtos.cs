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
using System.Collections.Generic;

namespace OpenAstroAra.Server.Contracts;

/// <summary>
/// §37 multi-profile management (the §30 "known profiles" list). Identity +
/// timestamps for a saved profile; the settings themselves are a
/// <see cref="ProfileSnapshotDto"/>.
/// </summary>
public sealed record ProfileMetaDto(
    Guid Id,
    string Name,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

/// <summary>
/// On-disk representation of one saved profile: identity + the full §37
/// settings snapshot. Persisted to <c>{profileDir}/profiles/{id}.json</c>.
/// </summary>
public sealed record StoredProfileDto(
    ProfileMetaDto Meta,
    ProfileSnapshotDto Settings);

/// <summary>Response for <c>GET /api/v1/profiles</c> — the known-profiles list
/// plus which one is currently active.</summary>
public sealed record ProfileListDto(
    Guid? ActiveId,
    IReadOnlyList<ProfileMetaDto> Profiles);

/// <summary>
/// Body for <c>POST /api/v1/profiles</c>. <paramref name="Settings"/> is the
/// full settings to store; when null the server clones the current active
/// profile's settings (a "duplicate current" create). The wizard Save (PR B)
/// passes a fully-populated snapshot built from its draft.
/// </summary>
public sealed record CreateProfileRequestDto(
    string Name,
    ProfileSnapshotDto? Settings = null);

/// <summary>Body for <c>PUT /api/v1/profiles/{id}</c> — rename.</summary>
public sealed record RenameProfileRequestDto(string Name);
