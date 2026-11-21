#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace OpenAstroAra.Server.Services;

/// <summary>Shared argv pieces for every <c>systemctl</c> call the daemon makes.</summary>
internal static class Systemctl {

    /// <summary>
    /// First argument of every <c>systemctl</c> call. On the rig the shipped polkit rules
    /// (<c>packaging/debian/usr/share/polkit-1/rules.d/50-openastroara-*.rules</c>) answer YES for the
    /// openastroara user without asking, so this changes nothing there. Anywhere else (a developer's
    /// desktop with a polkit agent, a test run) polkit would otherwise pop an interactive password
    /// dialog; with this flag systemctl fails fast with "Interactive authentication required" instead.
    /// </summary>
    internal const string NoAskPassword = "--no-ask-password";
}