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
using System.Collections.Generic;

namespace OpenAstroAra.Sequencer.Interfaces {

    /// <summary>
    /// One analysed frame of the current imaging session. <see cref="Id"/> is a
    /// session-monotonic ordinal shared with <see cref="AutofocusHistoryEntry.Id"/>,
    /// so "frames since the last autofocus" is a simple Id comparison.
    /// </summary>
    public sealed record ImageHistoryEntry(long Id, string Type, double Hfr, string? Filter);

    /// <summary>
    /// One completed autofocus run. <see cref="Id"/> is the image-ordinal watermark at the
    /// time the run finished; <see cref="Temperature"/> is the focuser temperature then
    /// (NaN when the focuser reports none) — the reference point for the §59.5
    /// temperature-delta trigger.
    /// </summary>
    public sealed record AutofocusHistoryEntry(long Id, DateTimeOffset Time, double Temperature, string? Filter);

    /// <summary>
    /// §59.5 — the headless replacement for NINA's <c>IImageHistoryVM</c>, carrying exactly
    /// what the autofocus triggers read: per-frame HFR points of the running session and the
    /// completed-autofocus record. Implemented server-side (the daemon owns frame analysis
    /// and the §59 sweep); triggers treat a missing seam as "no history" and stay quiet
    /// rather than fire on data they cannot see.
    /// </summary>
    public interface IImageHistory {

        /// <summary>Analysed session frames, oldest first. May be trimmed at the front.</summary>
        IReadOnlyList<ImageHistoryEntry> ImagePoints { get; }

        /// <summary>Completed autofocus runs of the session, oldest first.</summary>
        IReadOnlyList<AutofocusHistoryEntry> AutofocusPoints { get; }
    }
}
