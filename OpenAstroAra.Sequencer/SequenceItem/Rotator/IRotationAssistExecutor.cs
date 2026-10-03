#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.SequenceItem.Rotator {

    /// <summary>
    /// The by-hand rotation readout a <see cref="RotateCameraByHand"/> instruction drives on a rig
    /// without a rotator: the daemon plate-solves the main camera in a loop and reports the solved
    /// position angle against the target so the user can turn the camera by hand. Behind an
    /// interface (like <see cref="Platesolving.ICenteringExecutor"/>) so the instruction stays
    /// unit-testable and the Sequencer library never depends on the daemon's solver stack.
    /// </summary>
    public interface IRotationAssistExecutor {

        /// <summary>Start the readout loop toward <paramref name="targetPositionAngleDeg"/>.</summary>
        Task StartAsync(double targetPositionAngleDeg, CancellationToken token);

        /// <summary>Stop the loop (idempotent; waits for the in-flight solve to drain).</summary>
        Task StopAsync();
    }
}
