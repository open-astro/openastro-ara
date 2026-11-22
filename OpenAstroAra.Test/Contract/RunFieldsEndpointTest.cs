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
using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace OpenAstroAra.Test.Contract {

    /// <summary>#2168 — POST /api/v1/sequences/{id}/run/fields is mapped and speaks the live-edit
    /// answers: a sequence with no run gets the 409 "no active run" problem, not a 404.</summary>
    [TestFixture]
    [Category("IO")]
    [NonParallelizable] // AraHost sets process-wide state (profile dir, Serilog, Notifier)
    public class RunFieldsEndpointTest {

        [Test]
        public async Task Run_fields_on_a_sequence_with_no_run_is_a_409_no_active_run() {
            using var host = new AraHost();
            using var client = host.CreateClient();
            using var body = new StringContent(
                """{"changes":[{"path":[0],"target":"item","index":null,"key":"ExposureTime","value":180}]}""",
                Encoding.UTF8, "application/json");

            using var response = await client.PostAsync(new Uri($"/api/v1/sequences/{Guid.NewGuid()}/run/fields", UriKind.Relative), body);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("sequence-run-not-active"));
        }
    }
}
