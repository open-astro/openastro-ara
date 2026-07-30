#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NUnit.Framework;
using OpenAstroAra.Server.Endpoints;

namespace OpenAstroAra.Test {

    /// <summary>§63.19 (#885 review) — the phd2 PUT's write-boundary normalization of the guide setup
    /// type: only the openapi enum tokens persist; an out-of-band PUT with an arbitrary string lands as
    /// the guide_scope default, matching the client's read-side coercion.</summary>
    [TestFixture]
    public class GuiderSetupTypeNormalizationTest {

        [TestCase("oag", ExpectedResult = "oag")]
        [TestCase("OAG", ExpectedResult = "oag")]
        [TestCase("  oag  ", ExpectedResult = "oag")]
        [TestCase("guide_scope", ExpectedResult = "guide_scope")]
        [TestCase("Guide_Scope", ExpectedResult = "guide_scope")]
        [TestCase("banana", ExpectedResult = "guide_scope")]
        [TestCase("", ExpectedResult = "guide_scope")]
        [TestCase(null, ExpectedResult = "guide_scope")]
        public string Normalize_coerces_to_the_known_enum(string? raw) =>
            ProfileEndpoints.NormalizeGuiderSetupType(raw);
    }
}
