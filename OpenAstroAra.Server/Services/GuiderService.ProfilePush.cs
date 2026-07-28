#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

// §63.17 (PR 2) — on-demand profile push: POST /guider/profile/push re-runs the §63.5 engine-config +
// equipment-selection push against the connected daemon so a settings edit takes effect without a full
// reconnect. The push itself is PHD2Guider.RepushGuiderEngineConfigAsync (best-effort per message, with the
// disconnect → reconnect window when a selection/setup message is queued); completion is announced with the
// guider.profile_pushed WS event carrying the attempted RPC method names.
public sealed partial class GuiderService {

    /// <summary>§63.17 — synchronous 202-style op: the push runs to completion before the accept returns
    /// (it's a handful of quick RPCs, seconds at most — not a §60.5 background job), then the
    /// <c>guider.profile_pushed</c> event reports the attempted method names. Disconnected guider throws
    /// InvalidOperationException (→ 409, typed).</summary>
    public async Task<OperationAcceptedDto> PushGuiderProfileAsync(string? idempotencyKey, CancellationToken ct) {
        var guider = RequireConnectedGuider();
        var methods = await guider.RepushGuiderEngineConfigAsync(ct).ConfigureAwait(false);
        var payload = new JsonObject {
            ["methods"] = new JsonArray(methods.Select(m => (JsonNode)JsonValue.Create(m)).ToArray()),
            ["ara_profile_id"] = _activeProfileIdResolver?.Invoke()?.ToString(),
        };
        await EmitCalibrationEventAsync(WsEventCatalog.GuiderProfilePushed, payload).ConfigureAwait(false);
        return Accepted("guider.profile.push", idempotencyKey);
    }
}
