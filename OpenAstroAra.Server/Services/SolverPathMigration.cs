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

namespace OpenAstroAra.Server.Services;

/// <summary>
/// #1121 — #1094 moved the default ASTAP path to Debian's <c>/usr/bin/astap_cli</c>, but a
/// profile written before then keeps <c>/usr/bin/astap</c> (the GUI package's binary, which the
/// <c>.deb</c> never installs) and every solve fails. <see cref="ProfileSnapshotNormalizer"/>
/// applies <see cref="Migrate"/> wherever a snapshot is born from disk or written, so the old
/// value is rewritten once and then persists as the new one. File checks are injected for tests.
/// </summary>
public static class SolverPathMigration {
    public const string LegacyAstapPath = "/usr/bin/astap";
    public const string AstapCliPath = "/usr/bin/astap_cli";

    /// <summary>Rewrites <see cref="LegacyAstapPath"/> to <see cref="AstapCliPath"/> when the
    /// former is absent and the latter present; otherwise returns <paramref name="ps"/> unchanged
    /// (the same instance). A user who installed the GUI package keeps their choice.</summary>
    public static PlateSolveSettingsDto Migrate(PlateSolveSettingsDto ps, Func<string, bool> fileExists) {
        if (!string.Equals(ps.PathOrEndpoint?.Trim(), LegacyAstapPath, StringComparison.Ordinal)
                || fileExists(LegacyAstapPath) || !fileExists(AstapCliPath)) {
            return ps;
        }
        return ps with { PathOrEndpoint = AstapCliPath };
    }

    /// <summary>The configured solver binary when it is set but not on disk, else null. Boot
    /// logs a warning for it: without the binary every solve fails before ASTAP even runs.</summary>
    public static string? MissingSolverBinary(PlateSolveSettingsDto ps, Func<string, bool> fileExists) {
        var path = ps.PathOrEndpoint?.Trim();
        return string.IsNullOrEmpty(path) || fileExists(path) ? null : path;
    }

    /// <summary>True when the configured solver path is a URL: a legacy astrometry.net endpoint kept
    /// from a NINA profile. ARA solves with ASTAP only, so "binary not found at https://…" would
    /// point the user at the wrong fix (#1215).</summary>
    public static bool IsEndpointUrl(string? pathOrEndpoint) =>
        Uri.TryCreate(pathOrEndpoint?.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>The boot warning for the configured solver, or null when nothing is wrong: the
    /// binary is missing, or the setting is a URL. The text is built here (not in Program.cs) so it
    /// is unit-testable; Program.cs only logs it.</summary>
    public static string? BootWarning(PlateSolveSettingsDto ps, Func<string, bool> fileExists) {
        var path = ps.PathOrEndpoint?.Trim();
        if (string.IsNullOrEmpty(path)) {
            return null;
        }
        if (IsEndpointUrl(path)) {
            return $"Plate solver is configured as a URL ({path}), which ARA does not support (astrometry.net endpoints are not used): every plate solve (centering, polar alignment) will fail. Set Options → Plate solving → solver path to {AstapCliPath} (apt install astap-cli).";
        }
        if (!fileExists(path)) {
            return $"Plate solver binary not found at {path}: every plate solve (centering, polar alignment) will fail. Install astap-cli (apt install astap-cli) or fix Options → Plate solving → solver path.";
        }
        return null;
    }
}
