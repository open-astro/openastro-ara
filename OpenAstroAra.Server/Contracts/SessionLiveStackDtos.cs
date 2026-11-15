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

// ↑ara#1787 Session Live Stack: status of the colour stack fed by the sequencer's saved lights. Additive; every
// field that needs a stacked light is null until one exists. LastOutcome is a lowercase token: reference | stacked |
// flipped | unregistered | wrong_size | too_large | not_colour | skipped_busy | dropped. Unsupported is computed from the
// camera's capabilities only (no_camera | not_colour | too_large), null when the camera can be stacked. StackSeq counts
// lights added and never goes back, so a poller can change-detect the frame on it. LightsStacked counts the reference.
public sealed record SessionLiveStackStatusDto(
    bool Enabled,
    string? Unsupported,
    Guid? SessionId,
    string? TargetName,
    string? FilterName,
    long StackSeq,
    int LightsStacked,
    int LightsUnregistered,
    int LightsSkippedBusy,
    long RejectedSamples,
    int? Width,
    int? Height,
    string? LastOutcome,
    string? LastLightAtUtc);
