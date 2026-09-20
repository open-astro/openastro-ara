#region "copyright"
/*
    Copyright © 2016 - 2025 Stefan Berg <isbeorn@hotmail.com> and the N.I.N.A. contributors
    Copyright © 2026 - present Open Astro contributors

    This file is part of the open-source OpenAstro Ara project.

    This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
    If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/
#endregion "copyright"
using FluentAssertions;
using NUnit.Framework;
using OpenAstroAra.Core.Utility;
using System.Runtime.InteropServices;

namespace OpenAstroAra.Test {

    [TestFixture]
    public class CoreUtilUserAgentTest {

        [Test]
        public void UserAgentNamesAraAndTheRealPlatform() {
            // #1056: the inherited string claimed Win64/x64 on the Linux daemon.
            var ua = CoreUtil.UserAgent;
            ua.Should().StartWith("OpenAstroAra/" + CoreUtil.Version);
            ua.Should().Contain(RuntimeInformation.OSDescription);
            ua.Should().Contain(RuntimeInformation.ProcessArchitecture.ToString());
            ua.Should().NotContain("N.I.N.A.").And.NotContain("Win64").And.NotContain("Win32");
        }
    }
}
