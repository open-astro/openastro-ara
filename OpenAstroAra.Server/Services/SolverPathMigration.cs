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
}
