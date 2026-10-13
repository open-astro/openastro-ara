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

/// <summary>§57.4 — <c>POST /platesolve/verify-position</c> body. <c>ExposureSeconds</c> defaults to the
/// spec's quick 2 s; clamped to [0.1, 30].</summary>
public record VerifyPositionRequestDto(double? ExposureSeconds = null);

/// <summary>§57.4 — where the mount actually points after a Stop Mount: a quick snapshot, solved. The
/// three positions the user compares: the solve, what the mount reports, and where the last slew was
/// aimed (null when the mount has no target bookkeeping). Separations in arcminutes (null when either
/// side is missing). <c>Success</c> false = the solve failed; the mount figures are still filled in.</summary>
public record VerifyPositionResultDto(
    bool Success,
    double? SolvedRaHours,
    double? SolvedDecDegrees,
    double? MountRaHours,
    double? MountDecDegrees,
    double? IntendedRaHours,
    double? IntendedDecDegrees,
    double? SolvedToMountArcmin,
    double? SolvedToIntendedArcmin,
    double ExposureSeconds,
    string? Note);
