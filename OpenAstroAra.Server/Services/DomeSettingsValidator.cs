#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Enums;
using OpenAstroAra.Server.Contracts;
using System;
using System.Globalization;

namespace OpenAstroAra.Server.Services;

/// <summary>#1689 — what a dome geometry must satisfy before it is saved, and the wire token for the mount type.</summary>
internal static class DomeSettingsValidator {

    internal const string Equatorial = "equatorial";
    internal const string ForkOnWedge = "fork_on_wedge";

    internal static MountType ParseMountType(string? token) =>
        string.Equals(token, ForkOnWedge, StringComparison.OrdinalIgnoreCase) ? MountType.ForkOnWedge : MountType.EQUATORIAL;

    /// <summary>The reason a dome block is refused, or null when it can be saved.</summary>
    internal static string? Error(DomeSettingsDto d) {
        if (!string.Equals(d.MountType, Equatorial, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(d.MountType, ForkOnWedge, StringComparison.OrdinalIgnoreCase)) {
            return "mount_type must be 'equatorial' or 'fork_on_wedge'.";
        }
        double[] mm = [d.DomeRadiusMm, d.ScopePositionNorthSouthMm, d.ScopePositionEastWestMm, d.ScopePositionUpDownMm,
            d.GemAxisMm, d.LateralAxisMm, d.DecOffsetHorizontalMm];
        foreach (var v in mm) {
            if (!double.IsFinite(v) || Math.Abs(v) > 100_000) {
                return "Dome distances must be numbers within 100 m.";
            }
        }
        if (d.DomeRadiusMm < 0) {
            return "The dome radius cannot be negative.";
        }
        if (!double.IsFinite(d.AzimuthToleranceDegrees) || d.AzimuthToleranceDegrees <= 0 || d.AzimuthToleranceDegrees > 90) {
            return "The azimuth tolerance must be above 0 and at most 90 degrees.";
        }
        if (d.DomeSyncTimeoutSeconds is < 1 or > 3600) {
            return "The dome sync timeout must be between 1 and 3600 seconds.";
        }
        var offset = Math.Sqrt(Math.Pow(d.ScopePositionNorthSouthMm, 2) + Math.Pow(d.ScopePositionEastWestMm, 2) + Math.Pow(d.ScopePositionUpDownMm, 2));
        if (offset > d.DomeRadiusMm) {
            return string.Create(CultureInfo.InvariantCulture,
                $"The scope sits outside the dome: its offset from the dome center is {offset:0} mm, more than the dome radius of {d.DomeRadiusMm:0} mm.");
        }
        if (d.FollowMount && d.DomeRadiusMm <= 0) {
            return "Set the dome radius before the dome follows the mount.";
        }
        return null;
    }
}