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
using System.Text.Json;

namespace OpenAstroAra.Server.Contracts;

/// <summary>
/// §55.1 multi-device WILMA settings sync. The daemon stores one opaque UI-preferences blob per profile and serves it
/// back so a user's other devices pick it up on connect. The server treats <see cref="Settings"/> as an opaque JSON
/// object — the client owns its shape — and the store is last-write-wins (the §67 trusted-LAN single-user model).
/// </summary>
public sealed record ClientSettingsDto(JsonElement Settings, DateTimeOffset? UpdatedUtc);

/// <summary>Body of <c>PUT /api/v1/client-settings</c>: the full replacement preferences object.</summary>
public sealed record ClientSettingsUpdateDto(JsonElement Settings);
