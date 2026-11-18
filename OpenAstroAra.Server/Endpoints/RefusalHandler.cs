#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.AspNetCore.Http;
using OpenAstroAra.Server.Contracts.Refusals;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Endpoints;

/// <summary>
/// ADR 0022 — the one place a <see cref="RefusalException"/> becomes an answer: problem+json with the cause's
/// status and title, the message as <c>detail</c>, and <c>reason</c> (the cause's token) plus the refusal's
/// parameters as extensions. Any other exception is left alone, so a fault keeps its own 5xx path.
/// </summary>
public static class RefusalHandler {

    public static async Task Middleware(HttpContext http, RequestDelegate next) {
        try {
            await next(http);
        } catch (RefusalException refusal) when (!http.Response.HasStarted) {
            // Once the headers are out no problem body can follow, so a late refusal leaves as it came.
            var extensions = new Dictionary<string, object?>(refusal.Parameters) { ["reason"] = refusal.Cause.Token };
            await Results.Problem(detail: refusal.Message, statusCode: refusal.Cause.Status, title: refusal.Cause.Title,
                extensions: extensions).ExecuteAsync(http);
        }
    }
}
