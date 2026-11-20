#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Utility;
using System;
using System.Text.RegularExpressions;

namespace OpenAstroAra.Server;

/// <summary>
/// Spellings of the product version (#2093). The root VERSION file holds <c>X.Y.Z</c> or
/// <c>X.Y.Z~betaN</c>; the build (Directory.Build.props) turns it into the SemVer display
/// spelling (<c>1.0.0-beta.1</c>) that the API, mDNS and the UI show. This class mirrors that
/// mapping so tests can check the built daemon against the file.
/// </summary>
public static partial class AraVersion {

    /// <summary>Same rule as AraVersionPattern in Directory.Build.props and scripts/version.py.</summary>
    [GeneratedRegex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:~beta([1-9][0-9]*))?$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionFileRegex();

    /// <summary>
    /// Maps the VERSION file content to the display spelling: <c>1.0.0</c> stays,
    /// <c>1.0.0~beta1</c> becomes <c>1.0.0-beta.1</c>. Surrounding whitespace (the trailing
    /// newline) is ignored; anything else that is not one valid version line throws.
    /// </summary>
    /// <exception cref="FormatException">The content is not <c>X.Y.Z</c> or <c>X.Y.Z~betaN</c>.</exception>
    public static string ToDisplay(string versionFileText) {
        ArgumentNullException.ThrowIfNull(versionFileText);
        var text = versionFileText.Trim();
        var match = VersionFileRegex().Match(text);
        if (!match.Success) {
            throw new FormatException($"VERSION must be one line X.Y.Z or X.Y.Z~betaN (N >= 1), got '{text}'.");
        }
        var core = $"{match.Groups[1].Value}.{match.Groups[2].Value}.{match.Groups[3].Value}";
        return match.Groups[4].Success ? $"{core}-beta.{match.Groups[4].Value}" : core;
    }

    /// <summary>
    /// The display version from an assembly's informational version (<c>1.0.0-beta.1+&lt;sha&gt;</c>):
    /// drops the <c>+</c> build metadata. Falls back to the first three parts of
    /// <paramref name="assemblyVersion"/>, then to <c>0.0.0</c>. The rule lives in Core
    /// (<see cref="AraDisplayVersion"/>) so CoreUtil.Version, logs and FITS headers match.
    /// </summary>
    public static string FromInformationalVersion(string? informationalVersion, Version? assemblyVersion) =>
        AraDisplayVersion.FromInformationalVersion(informationalVersion, assemblyVersion);
}
