#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.Interfaces {

    /// <summary>One analysed LIGHT frame's star shape (#1264): median eccentricity (0 = round, 1 = a
    /// line) and median FWHM in pixels, with the exposure it was taken at. <see cref="Sequence"/> rises
    /// with every analysed frame so a consumer can tell a new reading from the one it already saw.</summary>
    public sealed record FrameQuality(Guid FrameId, long Sequence, double Eccentricity, double FwhmPx, double ExposureSeconds, DateTimeOffset AnalyzedUtc);

    /// <summary>
    /// #1264 — the seam the <c>UnguidedExposureGuard</c> reads frame quality through and acts back on
    /// the catalog with. The daemon's §59.5 post-capture analysis feeds <see cref="LatestLight"/>; the
    /// analysis is asynchronous, so the newest reading may lag the exposure that just finished by a
    /// frame — the guard keys on <see cref="FrameQuality.Sequence"/>, not on "the last exposure".
    /// </summary>
    public interface IFrameQualityFeed {

        /// <summary>The most recently analysed LIGHT frame, or null before the first one.</summary>
        FrameQuality? LatestLight { get; }

        /// <summary>Tag a frame in the catalog (<c>tags_json</c>), e.g. <c>trailed</c>. Best-effort.</summary>
        Task FlagFrameAsync(Guid frameId, string tag, CancellationToken ct);

        /// <summary>Post a notification (the notification center). Best-effort.</summary>
        Task NotifyAsync(string title, string message, CancellationToken ct);
    }
}
