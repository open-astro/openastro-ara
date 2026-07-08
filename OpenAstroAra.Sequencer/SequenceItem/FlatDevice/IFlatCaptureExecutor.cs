#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Model;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.SequenceItem.FlatDevice {

    /// <summary>
    /// One §48.3 flat set: the auto-exposure probe target and the saved-capture count for
    /// the CURRENT filter/focus context (per-filter iteration is the sequence's concern —
    /// the §39.5 generator emits one <see cref="FlatPanelFlats"/> per filter block).
    /// </summary>
    /// <param name="TargetAdu">Mean-ADU target for the probe loop (§48.7 default 30000 ≈ 45% of 16-bit full scale).</param>
    /// <param name="TolerancePct">Acceptable deviation from <paramref name="TargetAdu"/> in percent.</param>
    /// <param name="FrameCount">Saved FLAT frames to capture once the exposure converges.</param>
    /// <param name="Brightness">Flat-panel brightness to request while capturing (device units; the panel service clamps).</param>
    /// <param name="MinExposureSec">Lower exposure bound for the probe loop.</param>
    /// <param name="MaxExposureSec">Upper exposure bound for the probe loop.</param>
    /// <param name="Gain">Camera gain for the saved flats (-1 = camera default; match the session's lights).</param>
    /// <param name="Offset">Camera offset for the saved flats (-1 = camera default).</param>
    public sealed record FlatSetRequest(
        double TargetAdu,
        double TolerancePct,
        int FrameCount,
        int Brightness,
        double MinExposureSec,
        double MaxExposureSec,
        int Gain = -1,
        int Offset = -1);

    /// <summary>
    /// §48.3 — the flat-capture seam <see cref="FlatPanelFlats"/> executes through, mirroring
    /// <see cref="Autofocus.IAutofocusExecutor"/>: the Sequencer owns the decision to capture a
    /// flat set; the daemon owns the machinery (panel light, probe exposures, mean-ADU metric,
    /// saved captures).
    /// </summary>
    public interface IFlatCaptureExecutor {

        /// <summary>
        /// Light the flat panel (when one is connected), probe exposures until the frame mean
        /// lands within tolerance of the target ADU, then capture the requested number of saved
        /// FLAT frames at the converged exposure. True on success; false when the probe cannot
        /// converge (bounds pinned, no light) or the capture path is unavailable — the
        /// implementation logs the reason and restores the panel light either way.
        /// </summary>
        Task<bool> CaptureFlatSetAsync(FlatSetRequest request, IProgress<ApplicationStatus> progress, CancellationToken token);
    }
}
