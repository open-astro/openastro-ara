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

        // §76.2 — the guide exposure range gate at the same write boundary: min/max must be
        // positive and ordered, because the range drives BOTH darks coverage and guiding bounds.
        [TestCase(1000, 6000, ExpectedResult = true)]
        [TestCase(500, 500, ExpectedResult = true)]
        [TestCase(2500, 1000, ExpectedResult = false)]
        [TestCase(0, 6000, ExpectedResult = false)]
        [TestCase(1000, 0, ExpectedResult = false)]
        [TestCase(-500, 2000, ExpectedResult = false)]
        public bool Guide_exposure_range_gate(int minMs, int maxMs) =>
            ProfileEndpoints.ValidateGuideExposureRange(new OpenAstroAra.Server.Contracts.Phd2SettingsDto(
                Host: "localhost", Port: 4400, Phd2Profile: "Default",
                DitherEnabled: true, DitherEveryNFrames: 1, DitherPixels: 5.0,
                SettlePixels: 1.5, SettleTimeSec: 10, SettleTimeoutSec: 60,
                ForceCalibrationEachSession: false,
                GuideExposureMinMs: minMs, GuideExposureMaxMs: maxMs)) is null;
    }
}
