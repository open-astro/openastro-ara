#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace OpenAstroAra.Server.Contracts.WsEvents;

// ADR 0014 step 2, family camera.exposure_*: the typed payloads of the three exposure events, tied to
// their tokens in WsEventCatalog.Payloads. Property names serialize snake_case through
// AraJsonSerializerContext; the wire JSON is the one the publisher built by hand before.

/// <summary>Payload of <c>camera.exposure_started</c>. <c>kind</c> is the lower-cased image type or the
/// probe label; <c>started_utc</c> is an ISO 8601 round-trip ("O") UTC string.</summary>
public sealed record ExposureStartedPayload(string FrameId, double ExposureSec, string StartedUtc, string Kind, string? FilterName);

/// <summary>Payload of <c>camera.exposure_complete</c>; <c>elapsed_ms</c> is shutter-open to pixels-downloaded.</summary>
public sealed record ExposureCompletePayload(string FrameId, double ExposureSec, string StartedUtc, string Kind, long ElapsedMs);

/// <summary>Payload of <c>camera.exposure_failed</c>.</summary>
public sealed record ExposureFailedPayload(string FrameId, string Kind, string Reason);