#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace OpenAstroAra.Server.Contracts;

// ↑ara#1169 live stacking (PORT_PLAYBOOK §64.17): status of the server-side stack that runs inside a Live View
// session. Additive; every field that needs a stacked frame is null until one exists. LastOutcome is a lowercase
// token: reference | stacked | unregistered | wrong_size | too_large | dropped. Enabled is true only while the
// stack is bound to the Live View session that is running now. StackSeq counts frames added to the stack and
// never goes back, so a poller can change-detect the preview on (SessionId, StackSeq).
public sealed record LiveStackStatusDto(
    bool Enabled,
    long SessionId,
    long StackSeq,
    int FramesStacked,
    int FramesUnregistered,
    long RejectedSamples,
    int? ReferenceStars,
    int? Width,
    int? Height,
    double? LastOffsetX,
    double? LastOffsetY,
    double? LastRotationDeg,
    string? LastOutcome,
    string? LastFrameAtUtc);
