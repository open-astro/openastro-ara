#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace OpenAstroAra.Server.Contracts;

/// <summary>
/// §37 profile-section DTOs. Phase 12h.6a wires the first section
/// (imaging-defaults) end-to-end (server-side store + endpoints); other
/// sections follow in 12h.6b-N, one per WILMA settings panel.
///
/// Mirrors the WILMA client's <c>ImagingDefaults</c> model
/// (<c>lib/state/settings/imaging_defaults_state.dart</c>) field-for-field.
/// The §69 default-is-no-tooltip principle on the client side determined
/// which fields exist; the server stores whatever the client sends.
/// </summary>
public sealed record ImagingDefaultsDto(
    int ExposureSeconds,
    int Gain,
    int Offset,
    int Bin,
    string FrameKind,
    double CoolerTargetC,
    double CoolerRampCPerMin,
    bool WarmupAtSessionEnd);
