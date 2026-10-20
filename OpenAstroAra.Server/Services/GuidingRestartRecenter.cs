#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Sequencer.Interfaces;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>The Dither instruction's re-center-before-restart, over the ONE shared
/// <see cref="TargetRecenter"/>. The gap that counts as long is the star-lost recovery's wait.</summary>
public sealed class GuidingRestartRecenter : IGuidingRestartRecenter {

    private readonly TargetRecenter _recenter;

    public GuidingRestartRecenter(TargetRecenter recenter) {
        _recenter = recenter ?? throw new ArgumentNullException(nameof(recenter));
    }

    public TimeSpan LongGap { get; init; } = GuiderService.DefaultStarLostRecoverAfter;

    internal TimeSpan Timeout { get; init; } = GuiderService.DefaultStarLostRecenterTimeout;

    public async Task<bool> RecenterAsync(Coordinates target, IProgress<ApplicationStatus>? progress, CancellationToken token) {
        var result = await _recenter.RecenterAsync(target, Timeout, token).ConfigureAwait(false);
        if (result.Outcome == TargetRecenterOutcome.Cancelled) {
            token.ThrowIfCancellationRequested();
        }
        return result.Outcome == TargetRecenterOutcome.Recentered;
    }
}
