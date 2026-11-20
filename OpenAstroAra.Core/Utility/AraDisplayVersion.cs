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

namespace OpenAstroAra.Core.Utility {

    /// <summary>
    /// The product version as people see it (#2093): the SemVer display spelling
    /// (<c>1.0.0</c>, <c>1.0.0-beta.1</c>) that the build writes into every assembly's
    /// informational version from the root VERSION file. Lives in Core so the daemon
    /// (OpenAstroAra.Server.AraVersion) and the NINA-forked libraries share one rule.
    /// </summary>
    public static class AraDisplayVersion {

        /// <summary>
        /// The display version from an assembly's informational version (<c>1.0.0-beta.1+&lt;sha&gt;</c>):
        /// drops the <c>+</c> build metadata. Falls back to the first three parts of
        /// <paramref name="assemblyVersion"/>, then to <c>0.0.0</c>.
        /// </summary>
        public static string FromInformationalVersion(string? informationalVersion, Version? assemblyVersion) {
            if (!string.IsNullOrWhiteSpace(informationalVersion)) {
                var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
                var display = (plus >= 0 ? informationalVersion[..plus] : informationalVersion).Trim();
                if (display.Length > 0) return display;
            }
            return assemblyVersion is null ? "0.0.0" : assemblyVersion.ToString(3);
        }
    }
}
