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

    /// <summary>
    /// §60.9 web fallback — the WS protocol version can arrive via the
    /// X-Ara-WS-Version header (native clients) OR the ws_version query
    /// parameter (browser WebSockets can't set request headers). The header
    /// wins when both are present.
    /// </summary>
    [TestFixture]
    public class WebSocketVersionResolutionTest {

        [TestCase("1", "", "1", TestName = "Header_only")]
        [TestCase("", "1", "1", TestName = "Query_only_web_fallback")]
        [TestCase("1", "2", "1", TestName = "Header_wins_over_query")]
        [TestCase("2", "1", "2", TestName = "A_wrong_header_is_not_rescued_by_the_query")]
        [TestCase("", "", "", TestName = "Neither_resolves_empty_and_is_rejected_upstream")]
        public void ResolveRequestedWsVersion(string header, string query, string expected) {
            Assert.That(WebSocketEndpoints.ResolveRequestedWsVersion(header, query), Is.EqualTo(expected));
        }
    }
}
