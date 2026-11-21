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
using OpenAstroAra.Server.Services;
using System.Collections.Generic;

namespace OpenAstroAra.Test.PolarAlign;

/// <summary>#2068 — a bench profile whose mount's clearance the user confirmed: no Polar Align motion without it. The
/// bench mounts (mocks, the fake GEM) report no identity, so their key is the unidentified mount's.</summary>
internal static class PolarAlignClearance {
    public static PolarAlignSettingsDto Confirmed(this PolarAlignSettingsDto settings,
            string mountKey = PolarAlignPreflightEvaluator.UnidentifiedMountKey, double maxCwAngleDeg = 60.0) =>
        settings with {
            MountClearance = new Dictionary<string, PolarAlignMountClearanceDto> {
                [mountKey] = new(maxCwAngleDeg, ClearanceConfirmed: true, ConfirmedMaxCwAngleDeg: maxCwAngleDeg),
            },
        };
}
