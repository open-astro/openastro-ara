#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"
using System;

namespace OpenAstroAra.Sequencer.SequenceItem {

    /// <summary>
    /// #1155 — the contract behind the run ETA's "honest zero" rule. An instruction that implements
    /// this declares that <see cref="ISequenceEntity.GetEstimatedDuration"/> is a REAL figure: zero
    /// means zero seconds (an exposure of 0 s, a wait whose time has passed, a readout-mode switch),
    /// and the estimator charges it nothing. An instruction without it returns the base class's
    /// <c>TimeSpan.Zero</c> placeholder, which means "no estimate" and is charged the nominal
    /// instruction cost. Replaces the hand-maintained type list, which a new instruction with a
    /// duration model silently fell out of.
    /// </summary>
    public interface IEstimatesOwnDuration {
        /// <summary>Always true: the member exists so the contract is more than a marker (CA1040) and a
        /// consumer can read the declaration off any item through the interface.</summary>
        bool EstimatesOwnDuration => true;

        /// <summary>True when the estimate is already "time left from now" (a WaitForTime reports
        /// target minus now), so the run ETA must NOT also subtract the elapsed running time —
        /// doing both counted the elapsed part twice. False (the default) for a fixed-length
        /// instruction such as an exposure.</summary>
        bool EstimateIsRemaining => false;
    }

    /// <summary>
    /// #1155 — when the item entered <c>RUNNING</c> (UTC), null while it is not running. The run
    /// ETA subtracts the elapsed part of a running instruction's estimate instead of charging it in
    /// full (a 300 s exposure 250 s in still costs 300 s otherwise).
    /// </summary>
    public interface IRunningSince {
        DateTimeOffset? RunningSinceUtc { get; }
    }
}
