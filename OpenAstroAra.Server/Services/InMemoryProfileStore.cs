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

namespace OpenAstroAra.Server.Services;

/// <summary>
/// v0.0.1 profile store. Pure in-memory — values reset to defaults on
/// every daemon restart. File-based persistence lands in Phase 13.
///
/// Defaults match the WILMA client's <c>ImagingDefaults</c> constructor
/// defaults exactly so a fresh daemon + fresh client agree on initial
/// state without a round-trip.
/// </summary>
public sealed class InMemoryProfileStore : IProfileStore {
    private readonly object _lock = new();

    private ImagingDefaultsDto _imagingDefaults = new(
        ExposureSeconds: 5,
        Gain: 100,
        Offset: 50,
        Bin: 1,
        FrameKind: "light",
        CoolerTargetC: -10.0,
        CoolerRampCPerMin: 1.0,
        WarmupAtSessionEnd: false);

    public ImagingDefaultsDto GetImagingDefaults() {
        lock (_lock) { return _imagingDefaults; }
    }

    public void PutImagingDefaults(ImagingDefaultsDto value) {
        lock (_lock) { _imagingDefaults = value; }
    }
}
