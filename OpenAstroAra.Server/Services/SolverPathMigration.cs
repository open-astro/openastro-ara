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
/// The daemon ships its own ASTAP solver: <c>/opt/openastroara/astap_cli</c>, OpenAstro's
/// multi-core fork built from the <c>third_party/astap</c> submodule (#1136 follow-up). Profiles
/// written earlier point at Debian's <c>/usr/bin/astap_cli</c> (the default since #1094), the GUI
/// package's <c>/usr/bin/astap</c> (#1121: never installed by the <c>.deb</c>, so every solve
/// failed) or the ASTAP <c>.deb</c>'s <c>/opt/astap/astap_cli</c>. <see cref="ProfileSnapshotNormalizer"/>
/// applies <see cref="Migrate"/> wherever a snapshot is born from disk or written, so a stock
/// path is rewritten once to the bundled solver and then persists. A path outside that list is
/// the user's own choice and stays. File checks are injected for tests.
/// </summary>
public static class SolverPathMigration {
    public const string LegacyAstapPath = "/usr/bin/astap";
    /// <summary>Debian's <c>astap-cli</c> package, the default before the solver was bundled.</summary>
    public const string DebianAstapCliPath = "/usr/bin/astap_cli";
    /// <summary>The ASTAP <c>.deb</c> from astap.nl (also where its D80 database lands).</summary>
    public const string AstapDebCliPath = "/opt/astap/astap_cli";
    /// <summary>The bundled solver next to the daemon: the default and the migration target.</summary>
    public const string AstapCliPath = "/opt/openastroara/astap_cli";

    /// <summary>The stock solver paths earlier releases wrote; any of them moves to the bundled
    /// binary once it is on disk.</summary>
    public static readonly string[] StockPaths = { LegacyAstapPath, DebianAstapCliPath, AstapDebCliPath };

    /// <summary>Rewrites a stock solver path (<see cref="StockPaths"/>) to the bundled
    /// <see cref="AstapCliPath"/> when that binary is present; otherwise returns <paramref name="ps"/>
    /// unchanged (the same instance). Any other path is the user's own choice and is never touched.</summary>
    public static PlateSolveSettingsDto Migrate(PlateSolveSettingsDto ps, Func<string, bool> fileExists) {
        var path = ps.PathOrEndpoint?.Trim();
        if (path == null || Array.IndexOf(StockPaths, path) < 0 || path == AstapCliPath || !fileExists(AstapCliPath)) {
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
            return $"Plate solver is configured as a URL ({path}), which ARA does not support (astrometry.net endpoints are not used): every plate solve (centering, polar alignment) will fail. Set Options → Plate solving → solver path to {AstapCliPath} (the solver bundled with the daemon).";
        }
        if (!fileExists(path)) {
            return $"Plate solver binary not found at {path}: every plate solve (centering, polar alignment) will fail. Reinstall openastroara-server (it bundles the solver at {AstapCliPath}) or fix Options → Plate solving → solver path.";
        }
        return null;
    }
}
