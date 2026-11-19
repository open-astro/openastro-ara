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

// ADR 0014 step 2, family end_night.*: the typed payload of end_night.progress, tied to its token in
// WsEventCatalog.Payloads. Property names serialize snake_case through AraJsonSerializerContext.

/// <summary>Payload of <c>end_night.progress</c>: one change of the End Night action. <c>state</c> is the action's
/// state token. <c>step</c> and <c>step_state</c> name the step that changed, and are null on a change of the
/// action itself (started, cancel requested, finished). <c>detail</c> is the step's sentence.
/// <c>GET /api/v1/end-night/status</c> answers with every step.</summary>
public sealed record EndNightProgressPayload(
    string RunId, string State, string? Step, string? StepState, string? Detail, bool Cancellable);