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
using OpenAstroAra.Server.Services;
using System;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Endpoints;

/// <summary>
/// #1194 — the §27 single-client policy on the REST surface. The policy stays <b>advisory</b> for
/// a request that carries no <c>X-Ara-Session</c> header: a pre-§27 client, a script, or a WILMA
/// whose connect was refused keeps full REST access, exactly as <see cref="ClientSessionService"/>
/// documents. What this guard closes is the stale-holder hole: a client that WAS the holder, was
/// taken over, and keeps driving the rig with its old session id while someone else holds the
/// slot. A mutating request (anything but GET/HEAD/OPTIONS under <c>/api/v1</c>) that presents a
/// session id other than the current holder's is refused with 409 <c>stale-session</c>; the client
/// re-claims through <c>POST /server/connect</c>. With no holder at all (the slot was released)
/// nothing is stale and every id passes — that is the free slot, see
/// <see cref="ClientSessionService.IsStaleSession"/>. The connect/disconnect routes themselves are
/// exempt — they are how a session becomes current or stops being one — and so is the §57
/// emergency stop: safety never waits on a session handshake.
/// </summary>
public static class SessionGuard {

    /// <summary>Route prefix the guard watches; everything else (health, docs, WS) is untouched.</summary>
    private const string ApiPrefix = "/api/v1";

    /// <summary>Pure decision, unit-tested: should this request be refused as a stale session?</summary>
    internal static bool ShouldReject(string method, PathString path, string sessionHeader, Func<Guid, bool> isStaleSession) {
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method)) {
            return false;
        }
        if (!path.StartsWithSegments(ApiPrefix, StringComparison.OrdinalIgnoreCase, out var rest)) {
            return false;
        }
        if (rest.StartsWithSegments("/server/connect", StringComparison.OrdinalIgnoreCase)
                || rest.StartsWithSegments("/server/disconnect", StringComparison.OrdinalIgnoreCase)
                // §57 — a panic stop is never refused for being the wrong client: whoever is at the
                // rig when something goes wrong must be able to stop it.
                || rest.StartsWithSegments("/server/emergency-stop", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }
        // No header → advisory path (back-compat, see the class doc). Unparseable → the client sent
        // garbage, not a session; treated the same (the daemon never issued it, nothing to be stale).
        if (!Guid.TryParse(sessionHeader, out var sessionId)) {
            return false;
        }
        return isStaleSession(sessionId);
    }

    /// <summary>The middleware: refuses stale-session mutations with a 409 problem, passes everything else on.</summary>
    public static async Task Middleware(HttpContext http, RequestDelegate next) {
        var sessions = http.RequestServices.GetService<ClientSessionService>();
        if (sessions is not null
                && ShouldReject(http.Request.Method, http.Request.Path, http.Request.Headers["X-Ara-Session"].ToString(), sessions.IsStaleSession)) {
            await Results.Problem(
                type: "https://openastro.net/errors/stale-session",
                title: "Session is no longer the controlling client",
                statusCode: StatusCodes.Status409Conflict,
                detail: "The X-Ara-Session on this request is not the current §27 control session (another client took over, or this session was released). Re-claim the slot with POST /api/v1/server/connect before sending commands.")
                .ExecuteAsync(http);
            return;
        }
        await next(http);
    }
}
